namespace Poiesis.Engine.Ecs;

/// <summary>
/// Identifiant d'entité : un index de slot et une génération. Une entité détruite rend son slot,
/// et la génération invalide les anciennes références. <see cref="Null"/> veut dire « aucune ».
/// </summary>
public readonly record struct Entity(int Index, int Generation)
{
    public static readonly Entity Null = default;

    public bool IsNull => Generation == 0;

    public override string ToString() => IsNull ? "Entity(null)" : $"Entity({Index}:{Generation})";
}
