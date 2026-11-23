using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Ecs;

namespace Orionum.Game;

/// <summary>
/// Placeholder de la tranche verticale : chaque coque file vers l'ennemi le plus proche et s'arrête à portée.
/// Le vol newtonien du POC sera réécrit ici au jalon 2.
/// </summary>
public sealed class ApproachSystem : ISystem
{
    public void Update(World world, in TickContext ctx)
    {
        var catalog = world.Resource<ShipCatalog>();
        var hulls = world.Store<Hull>();
        var transforms = world.Store<Transform>();
        var dt = (float)ctx.Dt;

        Query.Each<Hull, Velocity>(world, (int e, ref Hull hull, ref Velocity vel) =>
        {
            var ship = catalog.Classes[hull.ClassIndex];
            var pos = transforms.Get(e).Position;
            float best = float.MaxValue;
            float3 target = pos;
            var entities = hulls.DenseEntities;
            var all = hulls.Components;
            for (int i = 0; i < entities.Length; i++)
            {
                if (all[i].Faction == hull.Faction || all[i].Health <= 0)
                    continue;
                var other = transforms.Get(entities[i]).Position;
                var d = float3.Distance(pos, other);
                if (d < best)
                {
                    best = d;
                    target = other;
                }
            }

            vel.Value = best == float.MaxValue || best <= ship.WeaponRange * 0.9f
                ? default
                : float3.Normalize(target - pos) * ship.CruiseSpeed;
        });

        Query.Each<Transform, Velocity>(world, (int _, ref Transform t, ref Velocity v) => t.Position += v.Value * dt);
    }
}

/// <summary>Placeholder : dégâts continus sur l'ennemi le plus proche à portée. Les boucliers à 6 faces viendront au jalon 2.</summary>
public sealed class CombatSystem : ISystem
{
    public void Update(World world, in TickContext ctx)
    {
        var catalog = world.Resource<ShipCatalog>();
        var hulls = world.Store<Hull>();
        var transforms = world.Store<Transform>();
        var entities = hulls.DenseEntities;
        var all = hulls.Components;
        var damage = new float[entities.Length];

        for (int i = 0; i < entities.Length; i++)
        {
            if (all[i].Health <= 0)
                continue;
            var ship = catalog.Classes[all[i].ClassIndex];
            var pos = transforms.Get(entities[i]).Position;
            int target = -1;
            float best = ship.WeaponRange;
            for (int j = 0; j < entities.Length; j++)
            {
                if (all[j].Faction == all[i].Faction || all[j].Health <= 0)
                    continue;
                var d = float3.Distance(pos, transforms.Get(entities[j]).Position);
                if (d <= best)
                {
                    best = d;
                    target = j;
                }
            }
            if (target >= 0)
                damage[target] += ship.WeaponDps * (float)ctx.Dt;
        }

        // Application après la phase de choix : le résultat ne dépend pas de l'ordre de parcours.
        for (int i = 0; i < entities.Length; i++)
            all[i].Health -= damage[i];

        for (int i = entities.Length - 1; i >= 0; i--)
            if (all[i].Health <= 0)
                world.Destroy(world.EntityAt(entities[i]));
    }
}
