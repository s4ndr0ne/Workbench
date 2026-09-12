using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Middleware;
using Workbench.Options;
using Workbench.Services;

namespace Workbench.Tests;

public sealed class RequestBodyCaptureTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Downstream_buffering_survives_return(bool capture, bool knownLength)
    {
        using var original = new NonSeekableStream("hello");
        var context = Context(original);
        context.Request.ContentLength = knownLength ? 5 : null;
        Stream? downstreamBody = null;
        var middleware = Middleware(async ctx =>
        {
            ctx.Request.EnableBuffering();
            downstreamBody = ctx.Request.Body;
            Assert.Equal("hello", await ReadAll(ctx.Request.Body, 0));
            ctx.Request.Body.Position = 0;
        }, new RequestLogCollector(), capture);

        await middleware.InvokeAsync(context);

        Assert.Same(downstreamBody, context.Request.Body);
        Assert.True(context.Request.Body.CanSeek);
        Assert.Equal("hello", await ReadAll(context.Request.Body, 0));
        await context.Request.Body.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replaced_body_is_preserved_even_on_exception(bool fail)
    {
        using var original = new NonSeekableStream("hello");
        using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("replacement"));
        var context = Context(original);
        var middleware = Middleware(ctx =>
        {
            ctx.Request.Body = replacement;
            if (fail)
                throw new InvalidOperationException("downstream failure");
            return Task.CompletedTask;
        }, new RequestLogCollector());

        if (fail)
            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        else
            await middleware.InvokeAsync(context);

        Assert.Same(replacement, context.Request.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_wrapper_is_removed_when_still_owned_by_workbench(bool knownLength)
    {
        using var original = new NonSeekableStream("hello");
        var context = Context(original);
        context.Request.ContentLength = knownLength ? 5 : null;
        var collector = new RequestLogCollector();
        var middleware = Middleware(async ctx =>
        {
            Assert.NotSame(original, ctx.Request.Body);
            Assert.Equal("hello", await ReadAll(ctx.Request.Body, 0));
        }, collector);

        await middleware.InvokeAsync(context);

        Assert.Same(original, context.Request.Body);
        Assert.True(original.CanRead);
        Assert.Equal("hello", Assert.Single(collector.GetEntries()).RequestBody);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Seekable_body_rereads_do_not_duplicate_logged_data(int readMode)
    {
        using var original = new NonSeekableStream("hello");
        var context = Context(original);
        context.Request.EnableBuffering();
        using var buffered = context.Request.Body;
        var collector = new RequestLogCollector();
        var middleware = Middleware(async ctx =>
        {
            Assert.Equal("hello", await ReadAll(ctx.Request.Body, readMode));
            ctx.Request.Body.Position = 0;
            Assert.Equal("hello", await ReadAll(ctx.Request.Body, readMode));
            ctx.Request.Body.Seek(2, SeekOrigin.Begin);
            Assert.Equal("llo", await ReadAll(ctx.Request.Body, readMode));
        }, collector);

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(collector.GetEntries());
        Assert.Equal("hello", entry.RequestBody);
        Assert.Equal(5, entry.RequestSize);
        Assert.Same(buffered, context.Request.Body);
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(200000)]
    public async Task Capture_limit_applies_to_unique_body_positions(int length)
    {
        var text = new string('x', length);
        using var original = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var collector = new RequestLogCollector();
        var middleware = Middleware(async ctx =>
        {
            Assert.Equal(text, await ReadAll(ctx.Request.Body, 0));
            ctx.Request.Body.Position = 0;
            Assert.Equal(text, await ReadAll(ctx.Request.Body, 0));
        }, collector);

        await middleware.InvokeAsync(Context(original));

        var entry = Assert.Single(collector.GetEntries());
        Assert.Equal(length, entry.RequestSize);
        Assert.Equal(new string('x', Math.Min(length, 65536))
            + (length > 65536 ? "\n… (body truncated)" : ""), entry.RequestBody);
    }

    [Fact]
    public async Task Out_of_order_reads_are_captured_at_their_body_offsets()
    {
        using var original = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var collector = new RequestLogCollector();
        var middleware = Middleware(async ctx =>
        {
            ctx.Request.Body.Seek(2, SeekOrigin.Begin);
            Assert.Equal("llo", await ReadAll(ctx.Request.Body, 0));
            ctx.Request.Body.Position = 0;
            var prefix = new byte[2];
            await ctx.Request.Body.ReadExactlyAsync(prefix);
            Assert.Equal("he", Encoding.UTF8.GetString(prefix));
        }, collector);

        await middleware.InvokeAsync(Context(original));

        var entry = Assert.Single(collector.GetEntries());
        Assert.Equal("hello", entry.RequestBody);
        Assert.Equal(5, entry.RequestSize);
    }

    [Fact]
    public async Task Unread_gaps_are_not_logged_as_body_content()
    {
        using var original = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var collector = new RequestLogCollector();
        var middleware = Middleware(ctx =>
        {
            Assert.Equal('h', ctx.Request.Body.ReadByte());
            ctx.Request.Body.Seek(4, SeekOrigin.Begin);
            Assert.Equal('o', ctx.Request.Body.ReadByte());
            ctx.Request.Body.Seek(100, SeekOrigin.Begin);
            Assert.Equal(-1, ctx.Request.Body.ReadByte());
            return Task.CompletedTask;
        }, collector);

        await middleware.InvokeAsync(Context(original));

        var entry = Assert.Single(collector.GetEntries());
        Assert.Equal("h", entry.RequestBody);
        Assert.Equal(5, entry.RequestSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconsumed_body_is_not_read_by_workbench(bool knownLength)
    {
        using var original = new NonSeekableStream("hello");
        var context = Context(original);
        context.Request.ContentLength = knownLength ? 5 : null;
        var collector = new RequestLogCollector();
        var middleware = Middleware(_ =>
        {
            Assert.Equal(0, original.BytesConsumed);
            return Task.CompletedTask;
        }, collector);

        await middleware.InvokeAsync(context);

        Assert.Equal(0, original.BytesConsumed);
        var entry = Assert.Single(collector.GetEntries());
        Assert.Null(entry.RequestBody);
        Assert.Equal(0, entry.RequestSize);
    }

    private static DefaultHttpContext Context(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/echo";
        context.Request.Body = body;
        return context;
    }

    private static RequestLogMiddleware Middleware(RequestDelegate next, RequestLogCollector collector, bool capture = true) =>
        new(next, collector, NullLogger<RequestLogMiddleware>.Instance,
            Microsoft.Extensions.Options.Options.Create(new WorkbenchOptions { CaptureRequestBody = capture }));

    private static async Task<string> ReadAll(Stream stream, int mode)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = mode switch
            {
                0 => await stream.ReadAsync(buffer.AsMemory()),
                1 => await stream.ReadAsync(buffer, 0, buffer.Length),
                2 => stream.Read(buffer, 0, buffer.Length),
                3 => stream.Read(buffer.AsSpan()),
                _ => ReadByte(stream, buffer),
            };
            if (read == 0)
                break;
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static int ReadByte(Stream stream, byte[] buffer)
    {
        var value = stream.ReadByte();
        if (value < 0)
            return 0;
        buffer[0] = (byte)value;
        return 1;
    }

    private sealed class NonSeekableStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public long BytesConsumed => base.Position;
        public override bool CanSeek => false;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
