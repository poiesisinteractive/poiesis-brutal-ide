using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using Poiesis.Engine.Ecs;

namespace Orionum.Game;

/// <summary>Les six faces de bouclier, dans le repère de la coque.</summary>
public enum Face { Front, Back, Port, Starboard, Dorsal, Ventral }

public enum Subsystem { Engines, Weapons, Shields }

/// <summary>Une coque : sa classe, son camp, son escadre et ce qui reste d'elle.</summary>
public struct Ship
{
    public int Class;
    public Faction Faction;

    /// <summary>Escadre (index de la flotte dans la scène) : sert au commandement et à l'affichage.</summary>
    public int Group;

    public float Hull;

    /// <summary>Cible de manœuvre (la coque ennemie à engager), ou nulle.</summary>
    public Entity Target;

    /// <summary>En croisière : vol rapide vers l'ennemi, armes muettes.</summary>
    public bool Cruising;

    /// <summary>Poussée du moteur principal sur ce pas (0 à 1), pour les visuels.</summary>
    public float Thrust;

    public float DamageDealt;
}

/// <summary>Vol newtonien : vitesse, rotation et cap (le nez, +Z de la coque).</summary>
public struct Motion
{
    public float3 Velocity;
    public float3 AngularVelocity;
    public float3 Forward;
}

/// <summary>Charge des six faces de bouclier. Champs nommés : lisibles et éditables dans l'inspecteur d'entités.</summary>
public struct Shields
{
    public float Front, Back, Port, Starboard, Dorsal, Ventral;

    [UnscopedRef]
    public ref float this[Face face] => ref Unsafe.Add(ref Front, (int)face);

    public readonly float Total => Front + Back + Port + Starboard + Dorsal + Ventral;
}

/// <summary>Sous-systèmes : moteurs, armes, générateurs de bouclier. Hors service à 0, réparés lentement.</summary>
public struct Subsystems
{
    public float Engines, Weapons, ShieldGenerators;
    public bool EnginesOffline, WeaponsOffline, ShieldsOffline;

    [UnscopedRef]
    public ref float Hp(Subsystem s) => ref Unsafe.Add(ref Engines, (int)s);

    [UnscopedRef]
    public ref bool Offline(Subsystem s) => ref Unsafe.Add(ref EnginesOffline, (int)s);
}

/// <summary>État d'une batterie : recharge, tourelles vivantes (bits), usure de la tourelle en cours, cible, lance.</summary>
public struct WeaponState
{
    public float Cooldown;
    public ulong Alive;
    public float Wear;
    public Entity Target;
    public float BeamLeft;

    public readonly int AliveCount => System.Numerics.BitOperations.PopCount(Alive);
}

/// <summary>Les batteries d'une coque (au plus huit), rangées dans l'ordre de sa classe.</summary>
public struct WeaponBank
{
    public WeaponState W0, W1, W2, W3, W4, W5, W6, W7;

    [UnscopedRef]
    public ref WeaponState this[int i] => ref Unsafe.Add(ref W0, i);
}

/// <summary>Menace subie par face (décroît) : sert à présenter la meilleure face.</summary>
public struct Threat
{
    public float Front, Back, Port, Starboard, Dorsal, Ventral;

    [UnscopedRef]
    public ref float this[Face face] => ref Unsafe.Add(ref Front, (int)face);
}
