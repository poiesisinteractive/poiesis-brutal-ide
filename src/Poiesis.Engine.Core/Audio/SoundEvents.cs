using Brutal.Numerics;

namespace Poiesis.Engine.Audio;

/// <summary>Un son à jouer : le nom d'une cue de la banque de sons, où, et à quel volume relatif.</summary>
public readonly record struct SoundEvent(string Cue, float3 Position, float Volume);

/// <summary>
/// Les sons que la sim demande, en ressource du monde. Un anneau de taille fixe : la sim écrit sans jamais attendre
/// personne (rien ne grossit en headless, où nul ne les lit), et l'hôte lit ce qui est arrivé depuis sa dernière lecture.
/// N'influe pas sur la sim : déterminisme intact.
/// </summary>
public sealed class SoundEvents
{
    readonly SoundEvent[] _ring;
    long _next;

    public SoundEvents(int capacity = 1024) => _ring = new SoundEvent[capacity];

    /// <summary>Numéro du prochain événement : l'hôte le garde pour savoir où reprendre.</summary>
    public long Next => _next;

    public void Play(string cue, float3 position, float volume = 1f)
    {
        _ring[_next % _ring.Length] = new SoundEvent(cue, position, volume);
        _next++;
    }

    /// <summary>
    /// Ajoute à <paramref name="into"/> les événements depuis <paramref name="since"/> (les plus anciens, écrasés, sont
    /// perdus) et rend le numéro où reprendre la prochaine fois.
    /// </summary>
    public long ReadSince(long since, List<SoundEvent> into)
    {
        long from = Math.Max(since, _next - _ring.Length);
        if (since > _next)
            from = Math.Max(0, _next - _ring.Length);   // le monde a changé (nouvelle partie) : on repart de son début
        for (long i = from; i < _next; i++)
            into.Add(_ring[i % _ring.Length]);
        return _next;
    }
}
