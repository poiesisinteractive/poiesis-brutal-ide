using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Parallel;

namespace Orionum.Game;

/// <summary>
/// Accès communs des systèmes. Les boucles parallèles découpent la table des coques (ou des projectiles) en
/// tranches fixes ; chaque tranche n'écrit que dans ses propres éléments.
/// </summary>
abstract class BattleSystem : ISystem
{
    protected const int ShipChunk = 32;
    protected const int ProjectileChunk = 512;

    public abstract string Name { get; }

    public abstract void Update(World world, in TickContext ctx);

    protected static Catalog Catalog(World w) => w.Resource<Catalog>();
    protected static Battle Battle(World w) => w.Resource<Battle>();

    /// <summary>La coque la plus proche d'un camp donné, dans un rayon, par la grille large (égalités : plus petit slot).</summary>
    protected static Entity NearestEnemy(World world, Battle battle, float3 from, float radius, Faction enemyOf)
    {
        var ships = world.Store<Ship>();
        var state = (World: world, Ships: ships, Enemy: enemyOf, Best: -1, BestD2: float.MaxValue);
        battle.ShipsWide.Query(from, radius, ref state, static (ref (World World, ComponentStore<Ship> Ships, Faction Enemy, int Best, float BestD2) s, int index, float d2) =>
        {
            ref var o = ref s.Ships.Get(index);
            if (o.Faction == s.Enemy || o.Hull <= 0)
                return;
            if (d2 < s.BestD2 || d2 == s.BestD2 && index < s.Best)
                (s.Best, s.BestD2) = (index, d2);
        });
        return state.Best < 0 ? Entity.Null : world.EntityAt(state.Best);
    }
}

/// <summary>Début de pas : grilles spatiales et état des escadres.</summary>
sealed class GridSystem : BattleSystem
{
    int[] _indices = new int[256];
    float3[] _positions = new float3[256];

    public override string Name => "Grilles";

    public override void Update(World world, in TickContext ctx)
    {
        var battle = Battle(world);
        var ships = world.Store<Ship>();
        var transforms = world.Store<Transform>();
        var motions = world.Store<Motion>();
        int n = ships.Count;
        Ensure(n);
        var dense = ships.DenseEntities;
        int maxSlot = n == 0 ? 0 : dense[n - 1] + 1;
        if (battle.SlotPosition.Length < maxSlot)
        {
            battle.SlotPosition = new float3[Math.Max(maxSlot, battle.SlotPosition.Length * 2)];
            battle.SlotVelocity = new float3[battle.SlotPosition.Length];
        }
        for (int i = 0; i < n; i++)
        {
            _indices[i] = dense[i];
            _positions[i] = transforms.Get(dense[i]).Position;
            battle.SlotPosition[dense[i]] = _positions[i];
            battle.SlotVelocity[dense[i]] = motions.Get(dense[i]).Velocity;
        }
        battle.ShipsWide.Build(_indices.AsSpan(0, n), _positions.AsSpan(0, n));
        battle.ShipsNear.Build(_indices.AsSpan(0, n), _positions.AsSpan(0, n));

        // Escadres : barycentre et effectif.
        var sums = new float3[battle.Groups.Length];
        for (int g = 0; g < battle.Groups.Length; g++)
            battle.Groups[g].Alive = 0;
        var items = ships.Components;
        for (int i = 0; i < n; i++)
        {
            ref var g = ref battle.Groups[items[i].Group];
            g.Alive++;
            sums[items[i].Group] += _positions[i];
        }
        for (int g = 0; g < battle.Groups.Length; g++)
            if (battle.Groups[g].Alive > 0)
                battle.Groups[g].Centroid = sums[g] / battle.Groups[g].Alive;

        // Missiles en vol, pour la défense ponctuelle.
        int m = 0;
        Ensure(battle.ProjectileCount);
        for (int i = 0; i < battle.ProjectileCount; i++)
        {
            ref var p = ref battle.Projectiles[i];
            if (p.Alive && p.Kind == WeaponKind.Missile)
            {
                _indices[m] = i;
                _positions[m++] = p.Position;
            }
        }
        battle.Missiles.Build(_indices.AsSpan(0, m), _positions.AsSpan(0, m));
    }

    void Ensure(int n)
    {
        if (_indices.Length >= n)
            return;
        Array.Resize(ref _indices, Math.Max(n, _indices.Length * 2));
        Array.Resize(ref _positions, _indices.Length);
    }
}

/// <summary>
/// Commandement (sans IA pour l'instant) : chaque escadre vise l'escadre ennemie la plus proche ; chaque coque
/// file en croisière tant que l'ennemi est loin, puis engage la coque ennemie la plus proche. Une coque ne
/// réévalue sa cible qu'une fois par seconde, en décalé, pour étaler le coût.
/// </summary>
sealed class CommandSystem : BattleSystem
{
    public override string Name => "Commandement";

    public override void Update(World world, in TickContext ctx)
    {
        var battle = Battle(world);
        var catalog = Catalog(world);
        var groups = battle.Groups;
        for (int g = 0; g < groups.Length; g++)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int o = 0; o < groups.Length; o++)
            {
                if (groups[o].Faction == groups[g].Faction || groups[o].Alive == 0)
                    continue;
                float d = float3.Distance(groups[g].Centroid, groups[o].Centroid);
                if (d < bestD)
                    (best, bestD) = (o, d);
            }
            groups[g].TargetGroup = best;
        }

        var ships = world.Store<Ship>();
        var transforms = world.Store<Transform>();
        var motions = world.Store<Motion>();
        long tick = ctx.Tick;
        Jobs.Shared.For(ships.Count, ShipChunk, (_, start, end) =>
        {
            var dense = ships.DenseEntities;
            var items = ships.Components;
            for (int i = start; i < end; i++)
            {
                ref var s = ref items[i];
                int e = dense[i];
                var pos = transforms.Get(e).Position;
                var cls = catalog.Classes[s.Class];
                var tg = groups[s.Group].TargetGroup;
                if (tg < 0)
                {
                    s.Target = Entity.Null;
                    s.Cruising = false;
                    continue;
                }

                // Croisière : tant que l'escadre ennemie est loin. On en sort à la vitesse de combat.
                float enemyDistance = float3.Distance(pos, groups[tg].Centroid);
                bool cruise = enemyDistance > battle.CruiseCutoff;
                if (s.Cruising && !cruise)
                {
                    ref var m = ref motions.Get(e);
                    m.Velocity = Frame.ClampLength(m.Velocity, cls.Def.Speed);
                }
                s.Cruising = cruise;

                bool stale = !world.IsAlive(s.Target) || ships.Get(s.Target.Index).Hull <= 0;
                if (!cruise && (stale || (tick + e) % 20 == 0))
                    s.Target = NearestEnemy(world, battle, pos, MathF.Max(cls.MaxRange * 2, 40_000), s.Faction);
            }
        });
    }
}

/// <summary>
/// Vol newtonien assisté (porté du POC) : l'ordinateur de vol transforme l'intention (vitesse voulue, cap voulu)
/// en poussées. Le moteur principal ne pousse que dans l'axe, une fois le nez aligné ; les propulseurs de
/// manœuvre, faibles, font le reste. La rotation accélère et freine (profil bang-bang).
/// </summary>
sealed class FlightSystem : BattleSystem
{
    public override string Name => "Vol";

    public override void Update(World world, in TickContext ctx)
    {
        var battle = Battle(world);
        var catalog = Catalog(world);
        var ships = world.Store<Ship>();
        var transforms = world.Store<Transform>();
        var motions = world.Store<Motion>();
        var subsystems = world.Store<Subsystems>();
        var shieldsStore = world.Store<Shields>();
        float dt = (float)ctx.Dt;

        Jobs.Shared.For(ships.Count, ShipChunk, (_, start, end) =>
        {
            var dense = ships.DenseEntities;
            var items = ships.Components;
            for (int i = start; i < end; i++)
            {
                ref var s = ref items[i];
                int e = dense[i];
                ref var t = ref transforms.Get(e);
                ref var m = ref motions.Get(e);
                var cls = catalog.Classes[s.Class];
                var subs = subsystems.Get(e);

                if (s.Cruising && s.Group < battle.Groups.Length && battle.Groups[s.Group].TargetGroup is var tg and >= 0)
                {
                    var dir = Frame.SafeNormalize(battle.Groups[tg].Centroid - t.Position, m.Forward);
                    m.Forward = dir;
                    m.AngularVelocity = default;
                    m.Velocity = dir * battle.CruiseSpeed;
                    t.Position += m.Velocity * dt;
                    t.Rotation = Frame.Rotation(m.Forward);
                    s.Thrust = 1;
                    continue;
                }

                var (vel, face) = Steer(world, battle, catalog, ships, transforms, motions, shieldsStore.Get(e), s, e, t.Position, m);
                FlightComputer(cls, subs, ref t, ref m, ref s, vel, face, dt);
                t.Rotation = Frame.Rotation(m.Forward);
            }
        });
    }

    /// <summary>Intention de vol : vitesse voulue et, si besoin, cap imposé.</summary>
    static (float3 Velocity, float3? Face) Steer(World world, Battle battle, Catalog catalog, ComponentStore<Ship> ships,
        ComponentStore<Transform> transforms, ComponentStore<Motion> motions, in Shields shields, in Ship s, int e, float3 pos, in Motion m)
    {
        var cls = catalog.Classes[s.Class].Def;
        if (!world.IsAlive(s.Target))
        {
            // Pas de cible : rejoindre l'escadre ennemie, ou freiner.
            if (s.Group < battle.Groups.Length && battle.Groups[s.Group].TargetGroup is var tg and >= 0)
                return (HoldAtDistance(cls, catalog.Classes[s.Class], pos, battle.Groups[tg].Centroid, cls.PreferredRange, s.Group), null);
            return (default, null);
        }

        int ti = s.Target.Index;
        var tpos = battle.SlotPosition[ti];
        var tvel = battle.SlotVelocity[ti];
        var to = tpos - pos;
        float d = to.Length();
        var toN = Frame.SafeNormalize(to, m.Forward);
        float range = cls.PreferredRange;
        switch (cls.Engage)
        {
            case EngageStyle.Nose:
            {
                var lead = tpos + tvel * (d / 1500);
                return (HoldAtDistance(cls, catalog.Classes[s.Class], pos, tpos, range * 0.92f, e) + tvel, Frame.SafeNormalize(lead - pos, toN));
            }
            case EngageStyle.Broadside:
            {
                if (d > range * 1.15f)
                    return (toN * cls.Speed, null);
                var tangent = OrbitTangent(e, toN);
                var radial = toN * ((d - range * 0.9f) * 0.3f);
                return (tangent * (cls.Speed * 0.35f) + radial + tvel, BroadsideFacing(m.Forward, toN));
            }
            case EngageStyle.Orbit:
            {
                if (d > range * 1.6f)
                    return (toN * cls.Speed, null);
                var tangent = OrbitTangent(e, toN);
                float weave = MathF.Sin(e * 1.7f + d * 0.01f) * 0.25f;
                var radial = toN * ((d - range * 0.75f) / MathF.Max(range * 0.75f, 1) * cls.Speed * 1.5f);
                return (tangent * cls.Speed + radial + Frame.WorldUp * (weave * cls.Speed) + tvel, null);
            }
            default:
                return (HoldAtDistance(cls, catalog.Classes[s.Class], pos, tpos, range, e) + tvel, toN);
        }
    }

    static float3 OrbitTangent(int e, float3 toN)
    {
        var t = float3.Cross(toN, Frame.WorldUp);
        t = t.Length() < 1e-3f ? new float3(1, 0, 0) : float3.Normalize(t);
        return e % 2 == 0 ? t : -t;
    }

    /// <summary>Garder le flanc vers la menace, du côté qui demande le moins de rotation.</summary>
    static float3 BroadsideFacing(float3 forward, float3 threat)
    {
        var a = float3.Cross(threat, Frame.WorldUp);
        a = a.Length() < 1e-3f ? new float3(1, 0, 0) : float3.Normalize(a);
        return float3.Dot(a, forward) >= 0 ? a : -a;
    }

    /// <summary>Vitesse qui pose la coque à <paramref name="want"/> de la cible, sans dépasser la vitesse sûre.</summary>
    static float3 HoldAtDistance(ShipClass cls, CompiledClass compiled, float3 pos, float3 target, float want, int salt)
    {
        var to = target - pos;
        float err = to.Length() - want;
        float v = MathF.Min(cls.Speed, SafeSpeed(cls, compiled, MathF.Abs(err)));
        return Frame.SafeNormalize(to, new float3(0, 0, 1)) * (MathF.Sign(err) * v);
    }

    /// <summary>Temps de retournement complet (bang-bang), pour la vitesse sûre.</summary>
    static float FlipTime(ShipClass cls, CompiledClass compiled)
    {
        float wmax = compiled.TurnRate;
        float aa = MathF.Max(1e-3f, cls.AngAccel);
        float tAcc = wmax / aa;
        return wmax * tAcc >= MathF.PI ? 2 * MathF.Sqrt(MathF.PI / aa) : tAcc + MathF.PI / wmax;
    }

    /// <summary>Vitesse dont on peut encore s'arrêter en <paramref name="d"/> mètres, retournement compris.</summary>
    static float SafeSpeed(ShipClass cls, CompiledClass compiled, float d)
    {
        float a = cls.Accel * 0.85f;
        float t = FlipTime(cls, compiled);
        return MathF.Max(0, a * (-t + MathF.Sqrt(t * t + 2 * MathF.Max(0, d) / a)));
    }

    static void FlightComputer(CompiledClass compiled, in Subsystems subs, ref Transform t, ref Motion m, ref Ship s, float3 wantVel, float3? face, float dt)
    {
        var cls = compiled.Def;
        float eng = Rules.EngineMult(subs);
        float main = cls.Accel * eng;
        float rcs = cls.Accel * cls.Rcs * eng;
        var vDes = Frame.ClampLength(wantVel, cls.Speed * eng);
        var dv = vDes - m.Velocity;
        float dvLen = dv.Length();

        // Cap : imposé, sinon dans l'axe de la poussée quand le changement est grand, sinon dans l'axe du déplacement.
        var heading = face ?? (dvLen > main * 0.8f ? dv / dvLen : vDes.Length() > 2 ? float3.Normalize(vDes) : m.Forward);
        Rotate(compiled, eng, ref m, heading, dt);

        // Le moteur principal ne s'allume qu'une fois le nez aligné : tourner d'abord, pousser ensuite.
        float along = float3.Dot(dv, m.Forward);
        float align = dvLen > 1e-6f ? along / dvLen : 0;
        float gate = Math.Clamp((align - 0.9f) / 0.08f, 0, 1);
        float mainA = along > 0 ? MathF.Min(main, along / dt) * gate : 0;
        var rest = dv - m.Forward * (mainA * dt);
        var rcsA = Frame.ClampLength(rest / dt, rcs);
        m.Velocity += (m.Forward * mainA + rcsA) * dt;
        t.Position += m.Velocity * dt;
        s.Thrust = main > 0 ? mainA / main : 0;
    }

    static void Rotate(CompiledClass compiled, float eng, ref Motion m, float3 face, float dt)
    {
        float wmax = compiled.TurnRate * eng;
        float aa = compiled.Def.AngAccel * eng;
        float c = Math.Clamp(float3.Dot(m.Forward, face), -1, 1);
        float angle = MathF.Acos(c);
        var axis = float3.Cross(m.Forward, face);
        if (axis.Length() < 1e-6f)
            axis = angle > 1 ? Frame.FromForward(m.Forward).Up : default;
        axis = axis.Length() > 0 ? float3.Normalize(axis) : axis;
        float wantRate = MathF.Min(wmax, MathF.Sqrt(2 * aa * angle) * 0.9f);
        var wantVel = axis * (angle < 0.002f ? 0 : wantRate);
        m.AngularVelocity += Frame.ClampLength(wantVel - m.AngularVelocity, aa * dt);
        float rate = m.AngularVelocity.Length();
        if (rate > 1e-6f)
            m.Forward = float3.Normalize(Frame.RotateAxis(m.Forward, m.AngularVelocity / rate, rate * dt));
    }
}
