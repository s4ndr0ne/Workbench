using Microsoft.AspNetCore.Mvc;

namespace Workbench.Sample.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private static readonly string[] Summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    [HttpGet]
    public IEnumerable<WeatherForecast> Get()
    {
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
        {
            Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            TemperatureC = Random.Shared.Next(-20, 55),
            Summary = Summaries[Random.Shared.Next(Summaries.Length)]
        })
        .ToArray();
    }

    [HttpGet("{id:int}")]
    public ActionResult<WeatherForecast> GetById(int id)
    {
        if (id < 0 || id >= Summaries.Length)
            return NotFound($"No forecast with id {id}");

        return new WeatherForecast
        {
            Date = DateOnly.FromDateTime(DateTime.Now),
            TemperatureC = 20 - id,
            Summary = Summaries[id],
        };
    }

    [HttpGet("search")]
    public ActionResult<dynamic> Search([FromQuery] int min = -20, [FromQuery] int max = 55)
    {
        return new
        {
            Query = new { Min = min, Max = max },
            MatchingSummaries = Summaries.Take(Math.Max(0, (max - min) / 10)).ToArray(),
        };
    }

    [HttpPost]
    public ActionResult<WeatherForecast> Create([FromBody] WeatherForecast forecast)
    {
        return CreatedAtAction(nameof(GetById), new { id = 3 }, forecast);
    }

    [HttpPut("{id:int}")]
    public ActionResult<WeatherForecast> Update(int id, [FromBody] WeatherForecast forecast)
    {
        if (id < 0 || id >= Summaries.Length)
            return NotFound();

        forecast.Date = DateOnly.FromDateTime(DateTime.Now);
        return forecast;
    }

    [HttpDelete("{id:int}")]
    public ActionResult Delete(int id)
    {
        if (id < 0 || id >= Summaries.Length)
            return NotFound();

        return NoContent();
    }
}
