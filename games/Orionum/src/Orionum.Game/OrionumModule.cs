using Poiesis.Engine.Debug;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

public sealed class OrionumModule : IGameModule, IWorldVisualizer, IGameReporter
{
    Catalog _catalog = new();
    readonly Visuals _visuals = new();

    public string Name => "Orionum";

    public void Load(GameModuleContext ctx) => _catalog = Catalog.Load(ctx.ContentRoot);

    public void InitializeWorld(World world)
    {
        world.SetResource(_catalog);
        world.SetResource(_catalog.Meshes);
        world.SetResource(new Battle());
    }

    /// <summary>L'ordre d'un pas de bataille (20 par seconde).</summary>
    public void BuildSchedule(World world, Schedule schedule) => schedule
        .Add(new GridSystem())
        .Add(new CommandSystem())
        .Add(new FlightSystem())
        .Add(new WeaponSystem())
        .Add(new ProjectileSystem())
        .Add(new DefenceSystem())
        .Add(new LifecycleSystem());

    public void Unload(GameModuleContext ctx) => _catalog = new Catalog();

    public IReadOnlyList<string> DebugChannels => _visuals.DebugChannels;

    public void Draw(World world, DrawContext ctx) => _visuals.Draw(world, ctx);

    public GameReport Report(World world) => BattleReport.Build(world);
}
