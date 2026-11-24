using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

/// <summary>Le bilan d'une bataille : issue, pertes, ce qui reste de chaque camp, et les dégâts infligés par rôle.</summary>
static class BattleReport
{
    public static GameReport Build(World world)
    {
        var battle = world.Resource<Battle>();
        var catalog = world.Resource<Catalog>();
        var m = new Dictionary<string, double>();
        var hullLeft = new double[2];
        var hullMax = new double[2];
        var alive = new int[2];
        foreach (var s in world.Store<Ship>().Components)
        {
            int f = (int)s.Faction;
            alive[f]++;
            hullLeft[f] += Math.Max(0, s.Hull);
            hullMax[f] += catalog.Classes[s.Class].Def.Hull;
        }
        foreach (var f in new[] { Faction.Coalition, Faction.Clans })
        {
            string n = f == Faction.Coalition ? "Coalition" : "Clans";
            m[$"{n} : coques restantes"] = alive[(int)f];
            m[$"{n} : pertes"] = battle.Losses[(int)f];
            m[$"{n} : coque restante des survivants (%)"] = hullMax[(int)f] > 0 ? 100 * hullLeft[(int)f] / hullMax[(int)f] : 0;
        }
        m["tirs"] = battle.ShotsFired;
        m["tourelles détruites"] = battle.TurretsDestroyed;
        m["missiles abattus"] = battle.MissilesIntercepted;

        string? winner = battle.Winner switch
        {
            Faction.Coalition => "Coalition",
            Faction.Clans => "Clans",
            _ => null,
        };
        return new GameReport(battle.Winner is not null, winner, battle.Winner is not null ? battle.EndTime : battle.Time, m);
    }
}
