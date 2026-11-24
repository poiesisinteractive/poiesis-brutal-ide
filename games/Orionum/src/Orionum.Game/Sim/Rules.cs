using Poiesis.Engine.Audio;
using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Ecs;

namespace Orionum.Game;

/// <summary>Réglages globaux du combat (repris des valeurs du POC).</summary>
public static class Tuning
{
    /// <summary>Un sous-système se répare tout seul jusqu'à cette part de ses points de vie.</summary>
    public const float SubsystemRepairCap = 0.5f;
    /// <summary>Réparation par seconde, en part des points de vie.</summary>
    public const float SubsystemRepairRate = 0.01f;
    /// <summary>Un sous-système hors service redémarre à cette part.</summary>
    public const float SubsystemRestart = 0.3f;
    /// <summary>Chance qu'un impact de coque touche aussi un sous-système au hasard, et la part des dégâts qu'il prend.</summary>
    public const float RandomSubsystemChance = 0.25f;
    public const float RandomSubsystemShare = 0.35f;
    /// <summary>Moteurs hors service : vitesse et virage ×0,2.</summary>
    public const float EnginesOfflineMult = 0.2f;
    public const float ThreatHalfLife = 4f;
    /// <summary>Au-delà de cette dispersion supplémentaire (rad), une tourelle renonce à une cible qu'elle ne peut pas suivre.</summary>
    public const float TrackingGiveUp = 0.05f;
    public const float MissileHp = 8f;
}

/// <summary>Règles partagées : faces de bouclier, arcs, suivi, dégâts.</summary>
public static class Rules
{
    public static float EngineMult(in Subsystems s) => s.EnginesOffline ? Tuning.EnginesOfflineMult : 1f;

    /// <summary>La face touchée par un coup venant de <paramref name="fromDir"/> (unitaire, de la coque vers la source).</summary>
    public static Face FaceFor(float3 forward, float3 fromDir)
    {
        var f = Frame.FromForward(forward);
        float a = float3.Dot(fromDir, f.Fwd), p = float3.Dot(fromDir, f.Port), u = float3.Dot(fromDir, f.Up);
        float aa = MathF.Abs(a), ap = MathF.Abs(p), au = MathF.Abs(u);
        if (aa >= ap && aa >= au)
            return a >= 0 ? Face.Front : Face.Back;
        if (ap >= au)
            return p >= 0 ? Face.Port : Face.Starboard;
        return u >= 0 ? Face.Dorsal : Face.Ventral;
    }

    /// <summary>Direction de visée de référence d'une batterie (pour son arc).</summary>
    public static float3 MountDirection(Mount mount, float3 forward, float3 toTarget)
    {
        var f = Frame.FromForward(forward);
        return mount switch
        {
            Mount.Nose => f.Fwd,
            Mount.Port => f.Port,
            Mount.Starboard => -f.Port,
            _ => Frame.SafeNormalize(toTarget, f.Fwd),
        };
    }

    public static bool InArc(CompiledWeapon w, float3 forward, float3 from, float3 target)
    {
        if (w.Def.ArcDeg >= 180)
            return true;
        var to = Frame.SafeNormalize(target - from, forward);
        return float3.Dot(MountDirection(w.Def.Mount, forward, to), to) >= w.ArcCos;
    }

    /// <summary>Dispersion supplémentaire quand la vitesse angulaire relative de la cible dépasse le suivi de l'arme.</summary>
    public static float TrackingPenalty(WeaponDef def, float3 shooterPos, float3 shooterVel, float3 targetPos, float3 targetVel)
    {
        if (def.Tracking <= 0)
            return 0;
        var los = targetPos - shooterPos;
        float d = los.Length();
        if (d < 1)
            return 0;
        los /= d;
        var rel = targetVel - shooterVel;
        float radial = float3.Dot(rel, los);
        float perp = MathF.Sqrt(MathF.Max(0, float3.Dot(rel, rel) - radial * radial));
        return MathF.Max(0, perp / d - def.Tracking) * 1.5f;
    }

    /// <summary>Écarte une direction d'un angle aléatoire au plus égal à <paramref name="spread"/>.</summary>
    public static float3 Jitter(float3 dir, float spread, int seed, long tick, int a, int b)
    {
        if (spread <= 0)
            return dir;
        var axis = Frame.SafeNormalize(float3.Cross(dir, Rng.Unit(seed, tick, a, b)), Frame.WorldUp);
        float angle = Rng.Float(seed, tick, a, b + 1) * spread;
        return float3.Normalize(Frame.RotateAxis(dir, axis, angle));
    }
}

/// <summary>Un coup à appliquer : produit en parallèle, appliqué ensuite dans un ordre fixe.</summary>
public struct Hit
{
    public Entity Target;
    public Entity Attacker;
    public float Amount;
    /// <summary>Direction de la coque touchée vers la source (unitaire).</summary>
    public float3 FromDir;
    public float VsShield;
    public float VsHull;
    public float Pierce;
}

/// <summary>Application des dégâts : bouclier de la face, débordement sur la coque, usure de la tourelle la plus proche, sous-systèmes.</summary>
public static class Damage
{
    public static void Apply(World world, Catalog catalog, Battle battle, in Hit hit)
    {
        if (!world.IsAlive(hit.Target) || hit.Amount <= 0)
            return;
        int e = hit.Target.Index;
        ref var ship = ref world.Store<Ship>().Get(e);
        if (ship.Hull <= 0)
            return;
        ref var motion = ref world.Store<Motion>().Get(e);
        ref var shields = ref world.Store<Shields>().Get(e);
        ref var subs = ref world.Store<Subsystems>().Get(e);
        ref var threat = ref world.Store<Threat>().Get(e);
        var cls = catalog.Classes[ship.Class];

        var face = Rules.FaceFor(motion.Forward, hit.FromDir);
        threat[face] += hit.Amount;
        if (world.IsAlive(hit.Attacker) && world.Store<Ship>().Has(hit.Attacker.Index))
            world.Store<Ship>().Get(hit.Attacker.Index).DamageDealt += hit.Amount;

        float vsShield = hit.VsShield <= 0 ? 1 : hit.VsShield;
        float vsHull = hit.VsHull <= 0 ? 1 : hit.VsHull;
        float pierced = hit.Amount * hit.Pierce;
        float onShield = (hit.Amount - pierced) * vsShield;
        float absorbed = MathF.Min(shields[face], onShield);
        shields[face] -= absorbed;
        var position = world.Store<Transform>().Get(e).Position;
        var sounds = world.TryResource<SoundEvents>(out var se) ? se : null;

        if (absorbed > 0 && battle.ShieldHits.Count < Battle.MaxImpactFx)
        {
            var frame = Frame.FromForward(motion.Forward);
            battle.ShieldHits.Add(new ShieldHitFx
            {
                Ship = hit.Target,
                LocalDir = new float3(float3.Dot(hit.FromDir, frame.Port), float3.Dot(hit.FromDir, frame.Up), float3.Dot(hit.FromDir, frame.Fwd)),
                Strength = Math.Clamp(absorbed / MathF.Max(cls.Def.ShieldFace, 1) * 4, 0.25f, 1.5f),
                Faction = ship.Faction,
                Life = 0.6f,
                MaxLife = 0.6f,
            });
            sounds?.Play("bouclier", position, MathF.Min(1, 0.3f + absorbed / 2000));
        }

        float overflow = (pierced + (onShield - absorbed) / vsShield) * vsHull;
        if (overflow <= 0)
            return;
        ship.Hull -= overflow;

        var at = ImpactPoint(cls.Def, position, motion.Forward, hit.FromDir, Rng.Float(battle.Seed, world.Tick, e, hit.Attacker.Index * 31 + 4));
        if (battle.HullHits.Count < Battle.MaxImpactFx)
            battle.HullHits.Add(new HullHitFx { Position = at, Size = MathF.Max(15, MathF.Min(overflow / 40, cls.Def.Radius * 0.15f)), Life = 0.4f, MaxLife = 0.4f });
        sounds?.Play("impact", at, MathF.Min(1, 0.3f + overflow / 3000));
        Wear(world, cls, battle, e, position, motion.Forward, at, overflow);

        if (Rng.Float(battle.Seed, world.Tick, e, hit.Attacker.Index * 31 + 5) < Tuning.RandomSubsystemChance)
        {
            var target = (Subsystem)(int)(Rng.Float(battle.Seed, world.Tick, e, hit.Attacker.Index * 31 + 6) * 3);
            ref var hp = ref subs.Hp(target);
            hp = MathF.Max(0, hp - overflow * Tuning.RandomSubsystemShare);
            if (hp <= 0)
                subs.Offline(target) = true;
        }
    }

    /// <summary>
    /// Point d'impact sur la coque, vue comme une capsule allongée (longueur et demi-largeur de la classe), du côté
    /// de la source, réparti au hasard le long de la coque. Une sphère englobante placerait les tirs de flanc
    /// d'un vaisseau de 3 km à 1,5 km de l'axe, loin de toute tourelle.
    /// </summary>
    public static float3 ImpactPoint(ShipClass cls, float3 position, float3 forward, float3 fromDir, float random)
    {
        float halfLength = cls.LengthMeters * 0.5f;
        float halfWidth = cls.LengthMeters * 0.12f;
        float along = Math.Clamp(float3.Dot(fromDir, forward) * halfLength + (random - 0.5f) * cls.LengthMeters * 0.8f, -halfLength, halfLength);
        var lateral = fromDir - forward * float3.Dot(fromDir, forward);
        float l = lateral.Length();
        return position + forward * along + (l > 1e-4f ? lateral / l * halfWidth : default);
    }

    /// <summary>
    /// Dégâts localisés : l'impact use la tourelle vivante la plus proche (à moins d'un demi-rayon de coque) ;
    /// au-delà de ses points de vie, elle saute. Le capital se désarme sous nos yeux.
    /// </summary>
    static void Wear(World world, CompiledClass cls, Battle battle, int e, float3 position, float3 forward, float3 at, float amount)
    {
        ref var bank = ref world.Store<WeaponBank>().Get(e);
        int bestWeapon = -1, bestTurret = -1;
        float bestD = cls.Def.Radius * 0.5f;
        for (int wi = 0; wi < cls.Weapons.Length; wi++)
        {
            ref var ws = ref bank[wi];
            if (ws.Alive == 0)
                continue;
            var hps = cls.Weapons[wi].Hardpoints;
            for (int t = 0; t < hps.Length; t++)
            {
                if ((ws.Alive & (1UL << t)) == 0)
                    continue;
                float d = float3.Distance(Frame.ToWorld(position, forward, hps[t]), at);
                if (d < bestD)
                    (bestWeapon, bestTurret, bestD) = (wi, t, d);
            }
        }
        if (bestWeapon < 0)
            return;
        ref var hit = ref bank[bestWeapon];
        var def = cls.Weapons[bestWeapon];
        hit.Wear += amount;
        if (hit.Wear < def.Def.TurretHp)
            return;
        hit.Wear = 0;
        hit.Alive &= ~(1UL << bestTurret);
        if (hit.Alive == 0)
            hit.BeamLeft = 0;
        battle.TurretsDestroyed++;
        if (world.TryResource<SoundEvents>(out var turretSounds))
            turretSounds.Play("tourelle", Frame.ToWorld(position, forward, def.Hardpoints[bestTurret]));
        battle.Explosions.Add(new ExplosionFx
        {
            Position = Frame.ToWorld(position, forward, def.Hardpoints[bestTurret]),
            Radius = MathF.Max(60, cls.Def.Radius * 0.12f),
            Life = 1.2f,
            MaxLife = 1.2f,
        });
    }
}
