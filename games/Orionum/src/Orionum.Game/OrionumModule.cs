using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

public sealed class OrionumModule : IGameModule
{
    ShipCatalog _catalog = new();

    public string Name => "Orionum";

    public void Load(GameModuleContext ctx) => _catalog = ShipCatalog.Load(Path.Combine(ctx.ContentRoot, "ships"));

    public void InitializeWorld(World world) => world.SetResource(_catalog);

    public void BuildSchedule(World world, Schedule schedule) =>
        schedule.Add(new ApproachSystem()).Add(new CombatSystem());

    public void Unload(GameModuleContext ctx) => _catalog = new ShipCatalog();
}
