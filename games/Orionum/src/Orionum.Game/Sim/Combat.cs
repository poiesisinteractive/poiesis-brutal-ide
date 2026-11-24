using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Parallel;

namespace Orionum.Game;

/// <summary>
/// Conduite de tir des batteries. En parallèle, chaque coque choisit ses cibles et produit des ordres de tir,
/// des coups de lance et des tirs de défense ponctuelle ; ensuite, dans l'ordre des coques, on fait naître les
/// projectiles et on applique les coups.
/// </summary>
sealed class WeaponSystem : BattleSystem
{
    struct FireOrder
    {
        public int Ship;      // slot du tireur
        public int Weapon;
        public Entity Target;
    }

    struct PdShot
    {
        public int Ship;
        public int Missile;   // index dans la table des projectiles
        public float Damage;
        public bool Hit;
    }

    sealed class ChunkOutput
    {
        public readonly List<FireOrder> Fire = new();
        public readonly List<Hit> Hits = new();
        public readonly List<BeamFx> Beams = new();
        public readonly List<PdShot> Pd = new();

        public void Clear()
        {
            Fire.Clear();
            Hits.Clear();
            Beams.Clear();
            Pd.Clear();
        }
    }

    readonly List<ChunkOutput> _chunks = new();

    public override string Name => "Armes";

    public override void Update(World world, in TickContext ctx)
    {
        var battle = Battle(world);
        var catalog = Catalog(world);
        var ships = world.Store<Ship>();
        var motions = world.Store<Motion>();
        var subsystems = world.Store<Subsystems>();
        var banks = world.Store<WeaponBank>();
        float dt = (float)ctx.Dt;
        long tick = ctx.Tick;

        int chunks = Jobs.ChunkCount(ships.Count, ShipChunk);
        while (_chunks.Count < chunks)
            _chunks.Add(new ChunkOutput());
        for (int c = 0; c < chunks; c++)
            _chunks[c].Clear();

        Jobs.Shared.For(ships.Count, ShipChunk, (chunk, start, end) =>
        {
            var output = _chunks[chunk];
            var dense = ships.DenseEntities;
            var items = ships.Components;
            for (int i = start; i < end; i++)
            {
                ref var s = ref items[i];
                int e = dense[i];
                var cls = catalog.Classes[s.Class];
                ref var bank = ref banks.Get(e);
                var subs = subsystems.Get(e);
                var pos = battle.SlotPosition[e];
                var fwd = motions.Get(e).Forward;

                for (int w = 0; w < cls.Weapons.Length; w++)
                {
                    ref var ws = ref bank[w];
                    if (ws.Alive == 0)
                        continue;
                    var weapon = cls.Weapons[w];
                    ws.Cooldown -= dt;

                    // Lance en cours : chaque émetteur vivant tient son rayon et inflige sa part, pas après pas.
                    if (ws.BeamLeft > 0)
                    {
                        ws.BeamLeft -= dt;
                        if (!LanceStep(world, battle, output, catalog, ws, weapon, e, s, pos, fwd, dt))
                            ws.BeamLeft = 0;
                    }

                    if (ws.Cooldown > 0 || s.Cruising || subs.WeaponsOffline)
                        continue;

                    if (weapon.Def.Kind == WeaponKind.PointDefence)
                    {
                        ws.Cooldown = weapon.Cooldown;
                        PointDefence(battle, output, weapon, e, s.Faction, pos, tick);
                        continue;
                    }

                    var target = PickTarget(world, battle, catalog, weapon, s, ws, pos, fwd, motions.Get(e).Velocity);
                    if (target.IsNull)
                    {
                        ws.Cooldown = 0.5f;   // rien à portée : on réessaie plus tard
                        continue;
                    }
                    // Canon axial : il faut pointer tout le vaisseau.
                    if (weapon.Def.Mount == Mount.Nose && !Rules.InArc(weapon, fwd, pos, battle.SlotPosition[target.Index]))
                    {
                        ws.Cooldown = 0.25f;
                        continue;
                    }
                    ws.Cooldown = weapon.Cooldown;
                    ws.Target = target;
                    output.Fire.Add(new FireOrder { Ship = e, Weapon = w, Target = target });
                }
            }
        });

        // Application, dans l'ordre des tranches (donc des coques) : résultat indépendant du nombre de threads.
        for (int c = 0; c < chunks; c++)
        {
            var output = _chunks[c];
            foreach (var order in output.Fire)
                Fire(world, battle, catalog, order, tick);
            foreach (var hit in output.Hits)
                Damage.Apply(world, catalog, battle, hit);
            battle.Beams.AddRange(output.Beams);
            foreach (var pd in output.Pd)
                ApplyPd(battle, pd);
        }
    }

    /// <summary>
    /// Conduite de tir : garder la cible de manœuvre ou la cible précédente tant qu'elle est à portée, dans l'arc et
    /// qu'on peut la suivre ; sinon la meilleure cible qu'on peut toucher (distance pondérée par la difficulté de suivi).
    /// </summary>
    static Entity PickTarget(World world, Battle battle, Catalog catalog, CompiledWeapon weapon, in Ship s, in WeaponState ws,
        float3 pos, float3 fwd, float3 vel)
    {
        var ships = world.Store<Ship>();
        var faction = s.Faction;
        bool Usable(Entity t)
        {
            if (!world.IsAlive(t) || !ships.Has(t.Index))
                return false;
            ref var o = ref ships.Get(t.Index);
            if (o.Faction == faction || o.Hull <= 0)
                return false;
            var tpos = battle.SlotPosition[t.Index];
            if (float3.Distance(pos, tpos) > weapon.Def.Range + catalog.Classes[o.Class].Def.Radius)
                return false;
            if (weapon.Def.Mount != Mount.Nose && !Rules.InArc(weapon, fwd, pos, tpos))
                return false;
            return Rules.TrackingPenalty(weapon.Def, pos, vel, tpos, battle.SlotVelocity[t.Index]) < Tuning.TrackingGiveUp;
        }

        if (Usable(s.Target))
            return s.Target;
        if (Usable(ws.Target))
            return ws.Target;

        var state = (World: world, Battle: battle, Catalog: catalog, Weapon: weapon, Faction: s.Faction, Pos: pos, Fwd: fwd, Vel: vel, Best: -1, Score: float.MaxValue);
        battle.ShipsWide.Query(pos, weapon.Def.Range + catalog.MaxShipRadius, ref state,
            static (ref (World World, Battle Battle, Catalog Catalog, CompiledWeapon Weapon, Faction Faction, float3 Pos, float3 Fwd, float3 Vel, int Best, float Score) st, int index, float d2) =>
            {
                ref var o = ref st.World.Store<Ship>().Get(index);
                if (o.Faction == st.Faction || o.Hull <= 0)
                    return;
                var radius = st.Catalog.Classes[o.Class].Def.Radius;
                float d = MathF.Sqrt(d2);
                if (d > st.Weapon.Def.Range + radius)
                    return;
                var tpos = st.Battle.SlotPosition[index];
                if (st.Weapon.Def.Mount != Mount.Nose && !Rules.InArc(st.Weapon, st.Fwd, st.Pos, tpos))
                    return;
                float penalty = Rules.TrackingPenalty(st.Weapon.Def, st.Pos, st.Vel, tpos, st.Battle.SlotVelocity[index]);
                float score = (d - radius) * (1 + penalty * 20);
                if (score < st.Score || score == st.Score && index < st.Best)
                    (st.Best, st.Score) = (index, score);
            });
        return state.Best < 0 ? Entity.Null : world.EntityAt(state.Best);
    }

    /// <summary>Un pas de lance : rend faux si le rayon s'éteint (cible perdue ou hors de portée).</summary>
    static bool LanceStep(World world, Battle battle, ChunkOutput output, Catalog catalog, in WeaponState ws, CompiledWeapon weapon,
        int e, in Ship s, float3 pos, float3 fwd, float dt)
    {
        if (!world.IsAlive(ws.Target) || world.Store<Ship>().Get(ws.Target.Index).Hull <= 0)
            return false;
        var tpos = battle.SlotPosition[ws.Target.Index];
        if (float3.Distance(pos, tpos) > weapon.Def.Range * 1.15f)
            return false;
        float radius = catalog.Classes[world.Store<Ship>().Get(ws.Target.Index).Class].Def.Radius;
        float share = weapon.Def.Damage * dt / weapon.Def.BeamDuration;
        for (int t = 0; t < weapon.Hardpoints.Length; t++)
        {
            if ((ws.Alive & (1UL << t)) == 0)
                continue;
            var from = Frame.ToWorld(pos, fwd, weapon.Hardpoints[t]);
            var dir = Frame.SafeNormalize(tpos - from, fwd);
            output.Hits.Add(new Hit
            {
                Target = ws.Target, Attacker = world.EntityAt(e), Amount = share, FromDir = -dir,
                VsShield = weapon.Def.VsShield, VsHull = weapon.Def.VsHull, Pierce = weapon.Def.Pierce,
            });
            output.Beams.Add(new BeamFx
            {
                From = from, To = tpos - dir * (radius * 0.85f), Faction = s.Faction, Life = dt * 1.5f, MaxLife = dt * 1.5f, Width = 2,
            });
        }
        return true;
    }

    /// <summary>Défense ponctuelle : le missile ennemi le plus proche, touché ou non selon la chance de l'arme.</summary>
    static void PointDefence(Battle battle, ChunkOutput output, CompiledWeapon weapon, int e, Faction faction, float3 pos, long tick)
    {
        var state = (Battle: battle, Faction: faction, Best: -1, BestD2: float.MaxValue);
        battle.Missiles.Query(pos, weapon.Def.Range, ref state, static (ref (Battle Battle, Faction Faction, int Best, float BestD2) st, int index, float d2) =>
        {
            ref var p = ref st.Battle.Projectiles[index];
            if (!p.Alive || p.Faction == st.Faction)
                return;
            if (d2 < st.BestD2 || d2 == st.BestD2 && index < st.Best)
                (st.Best, st.BestD2) = (index, d2);
        });
        if (state.Best < 0)
            return;
        bool hit = Rng.Float(battle.Seed, tick, e, state.Best) < weapon.Def.HitChance;
        output.Pd.Add(new PdShot { Ship = e, Missile = state.Best, Damage = weapon.Def.Damage, Hit = hit });
        output.Beams.Add(new BeamFx
        {
            From = pos, To = battle.Projectiles[state.Best].Position, Faction = faction, Life = 0.08f, MaxLife = 0.08f, Width = 1,
        });
    }

    static void ApplyPd(Battle battle, in PdShot pd)
    {
        ref var p = ref battle.Projectiles[pd.Missile];
        if (!pd.Hit || !p.Alive)
            return;
        p.Hp -= pd.Damage;
        if (p.Hp <= 0)
        {
            p.Alive = false;
            battle.MissilesIntercepted++;
        }
    }

    /// <summary>Le tir lui-même : projectiles par tourelle vivante, salve de missiles, ou rayon (lance, canon à particules).</summary>
    static void Fire(World world, Battle battle, Catalog catalog, in FireOrder order, long tick)
    {
        if (!world.IsAlive(order.Target))
            return;
        var ships = world.Store<Ship>();
        ref var s = ref ships.Get(order.Ship);
        var cls = catalog.Classes[s.Class];
        var weapon = cls.Weapons[order.Weapon];
        var def = weapon.Def;
        ref var ws = ref world.Store<WeaponBank>().Get(order.Ship)[order.Weapon];
        ref var motion = ref world.Store<Motion>().Get(order.Ship);
        var pos = battle.SlotPosition[order.Ship];
        var tpos = battle.SlotPosition[order.Target.Index];
        var tvel = battle.SlotVelocity[order.Target.Index];
        var shooter = world.EntityAt(order.Ship);
        float targetRadius = catalog.Classes[ships.Get(order.Target.Index).Class].Def.Radius;

        if (def.BeamDuration > 0)
        {
            ws.BeamLeft = def.BeamDuration;
            ws.Target = order.Target;
            battle.ShotsFired++;
            return;
        }

        if (def.Kind == WeaponKind.Missile)
        {
            var up = Frame.FromForward(motion.Forward).Up;
            int tube = 0;
            for (int i = 0; i < def.Salvo; i++)
            {
                tube = NextAlive(ws.Alive, tube, weapon.Hardpoints.Length);
                var from = Frame.ToWorld(pos, motion.Forward, weapon.Hardpoints[tube]);
                var dir = float3.Normalize(Frame.SafeNormalize(tpos - from, motion.Forward)
                    + Rng.Unit(battle.Seed, tick, order.Ship, order.Weapon * 64 + i) * 0.5f + up * 0.4f);
                ref var p = ref battle.Spawn();
                p.Position = from;
                p.Velocity = dir * (def.Speed * 0.4f) + motion.Velocity;
                p.Damage = def.Damage;
                p.VsShield = def.VsShield;
                p.VsHull = def.VsHull;
                p.Life = def.Range * 1.4f / def.Speed;
                p.Shooter = shooter;
                p.Faction = s.Faction;
                p.Kind = WeaponKind.Missile;
                p.Target = order.Target;
                p.Turn = def.Turn;
                p.Speed = def.Speed;
                p.Hp = Tuning.MissileHp;
                tube++;
            }
            battle.ShotsFired += def.Salvo;
            return;
        }

        for (int t = 0; t < weapon.Hardpoints.Length; t++)
        {
            if ((ws.Alive & (1UL << t)) == 0)
                continue;
            var from = Frame.ToWorld(pos, motion.Forward, weapon.Hardpoints[t]);
            if (def.Beam)
            {
                // Canon axial à particules : un rayon instantané, colossal.
                var bdir = Frame.SafeNormalize(tpos - from, motion.Forward);
                Damage.Apply(world, catalog, battle, new Hit
                {
                    Target = order.Target, Attacker = shooter, Amount = def.Damage, FromDir = -bdir,
                    VsShield = def.VsShield, VsHull = def.VsHull, Pierce = def.Pierce,
                });
                battle.Beams.Add(new BeamFx { From = from, To = tpos - bdir * (targetRadius * 0.85f), Faction = s.Faction, Life = 0.6f, MaxLife = 0.6f, Width = 4 });
                battle.ShotsFired++;
                continue;
            }
            float d = float3.Distance(from, tpos);
            var lead = tpos + tvel * (d / def.Speed);
            float spread = def.Spread + Rules.TrackingPenalty(def, pos, motion.Velocity, tpos, tvel);
            var dir = Rules.Jitter(Frame.SafeNormalize(lead - from, motion.Forward), spread, battle.Seed, tick, order.Ship, order.Weapon * 64 + t);
            ref var p = ref battle.Spawn();
            p.Position = from + dir * 12;
            p.Velocity = dir * def.Speed;
            p.Damage = def.Damage;
            p.VsShield = def.VsShield;
            p.VsHull = def.VsHull;
            p.Pierce = def.Pierce;
            p.Life = def.Range * 1.25f / def.Speed;
            p.Shooter = shooter;
            p.Faction = s.Faction;
            p.Kind = def.Kind;
            battle.ShotsFired++;
        }
    }

    static int NextAlive(ulong alive, int from, int count)
    {
        for (int k = 0; k < count; k++)
        {
            int t = (from + k) % count;
            if ((alive & (1UL << t)) != 0)
                return t;
        }
        return 0;
    }
}

/// <summary>
/// Projectiles : vol (les missiles virent vers leur cible), impacts contre les coques ennemies (segment contre
/// sphère, dans la grille fine), en parallèle ; les coups sont appliqués ensuite dans l'ordre de la table.
/// </summary>
sealed class ProjectileSystem : BattleSystem
{
    readonly List<List<Hit>> _hits = new();

    public override string Name => "Projectiles";

    public override void Update(World world, in TickContext ctx)
    {
        var battle = Battle(world);
        var catalog = Catalog(world);
        var ships = world.Store<Ship>();
        float dt = (float)ctx.Dt;
        int count = battle.ProjectileCount;
        int chunks = Jobs.ChunkCount(count, ProjectileChunk);
        while (_hits.Count < chunks)
            _hits.Add(new List<Hit>());
        for (int c = 0; c < chunks; c++)
            _hits[c].Clear();

        Jobs.Shared.For(count, ProjectileChunk, (chunk, start, end) =>
        {
            var hits = _hits[chunk];
            for (int i = start; i < end; i++)
            {
                ref var p = ref battle.Projectiles[i];
                if (!p.Alive)
                    continue;

                if (p.Kind == WeaponKind.Missile)
                    Steer(world, battle, ref p, dt);

                var from = p.Position;
                var step = p.Velocity * dt;
                p.Position += step;
                p.Life -= dt;

                // Impact : la première coque ennemie traversée sur ce pas.
                float len = step.Length();
                var state = (World: world, Ships: ships, Catalog: catalog, Battle: battle, From: from, Step: step, Faction: p.Faction, BestT: float.MaxValue, Best: -1);
                battle.ShipsNear.Query(from + step * 0.5f, len * 0.5f + catalog.MaxShipRadius, ref state,
                    static (ref (World World, Poiesis.Engine.Ecs.ComponentStore<Ship> Ships, Catalog Catalog, Battle Battle, float3 From, float3 Step, Faction Faction, float BestT, int Best) st, int index, float _) =>
                    {
                        ref var o = ref st.Ships.Get(index);
                        if (o.Faction == st.Faction || o.Hull <= 0)
                            return;
                        float r = st.Catalog.Classes[o.Class].Def.Radius * 0.9f;
                        float t = SegmentSphere(st.From, st.Step, st.Battle.SlotPosition[index], r);
                        if (t < st.BestT || t == st.BestT && index < st.Best)
                            (st.BestT, st.Best) = (t, index);
                    });
                if (state.Best >= 0)
                {
                    var target = world.EntityAt(state.Best);
                    var dir = Frame.SafeNormalize(p.Velocity, new float3(0, 0, 1));
                    hits.Add(new Hit
                    {
                        Target = target, Attacker = p.Shooter, Amount = p.Damage, FromDir = -dir,
                        VsShield = p.VsShield, VsHull = p.VsHull, Pierce = p.Pierce,
                    });
                    p.Alive = false;
                }
                else if (p.Life <= 0)
                {
                    p.Alive = false;
                }
            }
        });

        for (int c = 0; c < chunks; c++)
            foreach (var hit in _hits[c])
                Damage.Apply(world, catalog, battle, hit);
    }

    /// <summary>Missile : tourne vers sa cible (vitesse angulaire bornée) et accélère jusqu'à sa vitesse de croisière.</summary>
    static void Steer(World world, Battle battle, ref Projectile p, float dt)
    {
        float speed = p.Velocity.Length();
        var dir = speed > 1e-3f ? p.Velocity / speed : new float3(0, 0, 1);
        if (world.IsAlive(p.Target) && p.Target.Index < battle.SlotPosition.Length)
        {
            var want = Frame.SafeNormalize(battle.SlotPosition[p.Target.Index] - p.Position, dir);
            float angle = MathF.Acos(Math.Clamp(float3.Dot(dir, want), -1, 1));
            float turn = MathF.Min(angle, p.Turn * dt);
            if (angle > 1e-4f)
            {
                var axis = Frame.SafeNormalize(float3.Cross(dir, want), Frame.WorldUp);
                dir = float3.Normalize(Frame.RotateAxis(dir, axis, turn));
            }
        }
        speed = MathF.Min(p.Speed, speed + p.Speed * dt);
        p.Velocity = dir * speed;
    }

    /// <summary>Fraction du segment où il entre dans la sphère, ou +∞.</summary>
    static float SegmentSphere(float3 from, float3 step, float3 center, float radius)
    {
        var m = from - center;
        float a = float3.Dot(step, step);
        float b = float3.Dot(m, step);
        float c = float3.Dot(m, m) - radius * radius;
        if (c <= 0)
            return 0;   // déjà dedans
        if (a < 1e-9f || b > 0)
            return float.MaxValue;
        float disc = b * b - a * c;
        if (disc < 0)
            return float.MaxValue;
        float t = (-b - MathF.Sqrt(disc)) / a;
        return t is >= 0 and <= 1 ? t : float.MaxValue;
    }
}

/// <summary>Boucliers (régénération par face), réparations des sous-systèmes, menace, teinte des coques selon les dégâts.</summary>
sealed class DefenceSystem : BattleSystem
{
    public override string Name => "Défenses";

    public override void Update(World world, in TickContext ctx)
    {
        var catalog = Catalog(world);
        var ships = world.Store<Ship>();
        var shieldsStore = world.Store<Shields>();
        var subsStore = world.Store<Subsystems>();
        var threats = world.Store<Threat>();
        var renderables = world.Store<Renderable>();
        float dt = (float)ctx.Dt;
        float decay = MathF.Pow(0.5f, dt / Tuning.ThreatHalfLife);

        Jobs.Shared.For(ships.Count, ShipChunk, (_, start, end) =>
        {
            var dense = ships.DenseEntities;
            var items = ships.Components;
            for (int i = start; i < end; i++)
            {
                ref var s = ref items[i];
                int e = dense[i];
                var cls = catalog.Classes[s.Class].Def;
                ref var shields = ref shieldsStore.Get(e);
                ref var subs = ref subsStore.Get(e);
                ref var threat = ref threats.Get(e);

                float max = subs.ShieldsOffline ? 0 : cls.ShieldFace;
                float regen = subs.ShieldsOffline ? 0 : cls.ShieldRegen * dt;
                for (int f = 0; f < 6; f++)
                {
                    ref var face = ref shields[(Face)f];
                    face = MathF.Min(max, face + regen);
                    threat[(Face)f] *= decay;
                }

                for (int k = 0; k < 3; k++)
                {
                    var sub = (Subsystem)k;
                    ref var hp = ref subs.Hp(sub);
                    float cap = cls.SubsystemHp * Tuning.SubsystemRepairCap;
                    if (hp < cap)
                        hp = MathF.Min(cap, hp + cls.SubsystemHp * Tuning.SubsystemRepairRate * dt);
                    if (subs.Offline(sub) && hp >= cls.SubsystemHp * Tuning.SubsystemRestart)
                        subs.Offline(sub) = false;
                }

                // La coque s'assombrit et rougit à mesure qu'elle est détruite.
                ref var r = ref renderables.Get(e);
                var baseColor = Visuals.FactionHull(s.Faction);
                float health = Math.Clamp(s.Hull / cls.Hull, 0, 1);
                float k2 = 0.35f + 0.65f * health;
                r.Color = new float4(baseColor.X * k2 + (1 - health) * 0.25f, baseColor.Y * k2, baseColor.Z * k2, 1);
            }
        });
    }
}

/// <summary>Fin de pas : épaves, effets qui s'éteignent, table des projectiles compactée, issue de la bataille.</summary>
sealed class LifecycleSystem : BattleSystem
{
    readonly List<Entity> _dead = new();

    public override string Name => "Cycle de vie";

    public override void Update(World world, in TickContext ctx)
    {
        var battle = Battle(world);
        var catalog = Catalog(world);
        var ships = world.Store<Ship>();
        float dt = (float)ctx.Dt;

        _dead.Clear();
        var dense = ships.DenseEntities;
        var items = ships.Components;
        int[] alive = new int[2];
        for (int i = 0; i < dense.Length; i++)
        {
            if (items[i].Hull <= 0)
                _dead.Add(world.EntityAt(dense[i]));
            else
                alive[(int)items[i].Faction]++;
        }
        foreach (var e in _dead)
        {
            ref var s = ref ships.Get(e.Index);
            var radius = catalog.Classes[s.Class].Def.Radius;
            battle.Explosions.Add(new ExplosionFx { Position = battle.SlotPosition[e.Index], Radius = radius * 2.5f, Life = 4, MaxLife = 4 });
            battle.Losses[(int)s.Faction]++;
            world.Destroy(e);
        }

        battle.CompactProjectiles();
        Age(battle.Beams, dt);
        for (int i = battle.Explosions.Count - 1; i >= 0; i--)
        {
            var x = battle.Explosions[i];
            x.Life -= dt;
            if (x.Life <= 0)
                battle.Explosions.RemoveAt(i);
            else
                battle.Explosions[i] = x;
        }

        battle.Time += dt;
        if (battle.Winner is null && battle.Groups.Length > 0 && (alive[0] == 0 || alive[1] == 0))
        {
            battle.Winner = alive[0] > 0 ? Faction.Coalition : Faction.Clans;
            battle.EndTime = battle.Time;
        }
    }

    static void Age(List<BeamFx> beams, float dt)
    {
        int w = 0;
        for (int r = 0; r < beams.Count; r++)
        {
            var b = beams[r];
            b.Life -= dt;
            if (b.Life > 0)
                beams[w++] = b;
        }
        beams.RemoveRange(w, beams.Count - w);
    }
}
