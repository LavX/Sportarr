using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

internal static class LeagueMatchContext
{
    public static Task<List<League>> LoadAsync(
        SportarrDbContext db,
        CancellationToken cancellationToken = default)
    {
        return db.Leagues
            .AsNoTracking()
            .Select(league => new League
            {
                Id = league.Id,
                ExternalId = league.ExternalId,
                Name = league.Name,
                AlternateName = league.AlternateName,
                Sport = league.Sport
            })
            .ToListAsync(cancellationToken);
    }
}
