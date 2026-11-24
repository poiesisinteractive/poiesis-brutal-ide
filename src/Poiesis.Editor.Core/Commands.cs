using System.Reflection;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor;

/// <summary>Une modification annulable du document. Tout changement fait dans l'éditeur passe par une commande.</summary>
public interface IEditorCommand
{
    string Label { get; }
    void Do();
    void Undo();
}

public sealed class SetPropertyCommand(Node node, PropertyInfo property, object? newValue) : IEditorCommand
{
    readonly object? _oldValue = property.GetValue(node);

    public string Label => $"Modifier {node.Name}.{property.Name}";

    public void Do() => property.SetValue(node, newValue);

    public void Undo() => property.SetValue(node, _oldValue);
}

public sealed class RenameCommand(Node node, string newName) : IEditorCommand
{
    readonly string _oldName = node.Name;

    public string Label => $"Renommer {_oldName}";

    public void Do() => node.Name = newName;

    public void Undo() => node.Name = _oldName;
}

public sealed class AddNodeCommand(Node parent, Node child) : IEditorCommand
{
    public string Label => $"Ajouter {child.Name}";

    public void Do() => parent.AddChild(child);

    public void Undo() => parent.RemoveChild(child);
}

/// <summary>Insère un nœud à une position donnée parmi les enfants (coller après la sélection).</summary>
public sealed class InsertNodeCommand(Node parent, int index, Node child) : IEditorCommand
{
    public string Label => $"Ajouter {child.Name}";

    public void Do() => parent.InsertChild(index, child);

    public void Undo() => parent.RemoveChild(child);
}

public sealed class RemoveNodeCommand(Node node) : IEditorCommand
{
    readonly Node _parent = node.Parent ?? throw new InvalidOperationException("On ne supprime pas la racine");
    readonly int _index = node.Parent!.IndexOf(node);

    public string Label => $"Supprimer {node.Name}";

    public void Do() => _parent.RemoveChild(node);

    public void Undo() => _parent.InsertChild(_index, node);
}

/// <summary>Remplace un nœud par un autre, à la même place (enregistrer une branche comme scène).</summary>
public sealed class ReplaceNodeCommand(Node oldNode, Node newNode, string label) : IEditorCommand
{
    readonly Node _parent = oldNode.Parent ?? throw new InvalidOperationException("On ne remplace pas la racine");
    readonly int _index = oldNode.Parent!.IndexOf(oldNode);

    public string Label => label;

    public void Do()
    {
        _parent.RemoveChild(oldNode);
        _parent.InsertChild(_index, newNode);
    }

    public void Undo()
    {
        _parent.RemoveChild(newNode);
        _parent.InsertChild(_index, oldNode);
    }
}

/// <summary>« Rendre local » : l'instance devient des nœuds ordinaires de la scène, détachés de leur source.</summary>
public sealed class MakeLocalCommand(Node instance) : IEditorCommand
{
    readonly string? _path = instance.InstanceOf;

    public string Label => $"Rendre local {instance.Name}";

    public void Do() => instance.InstanceOf = null;

    public void Undo() => instance.InstanceOf = _path;
}

/// <summary>Plusieurs commandes annulées et rétablies d'un bloc (coller plusieurs nœuds, rétablir toutes les surcharges).</summary>
public sealed class CompositeCommand(string label, IReadOnlyList<IEditorCommand> commands) : IEditorCommand
{
    public string Label => label;

    public void Do()
    {
        foreach (var c in commands)
            c.Do();
    }

    public void Undo()
    {
        for (int i = commands.Count - 1; i >= 0; i--)
            commands[i].Undo();
    }
}

/// <summary>Déplace un nœud sous un autre parent (glisser-déposer dans l'arbre).</summary>
public sealed class ReparentCommand(Node node, Node newParent) : IEditorCommand
{
    readonly Node _oldParent = node.Parent ?? throw new InvalidOperationException("On ne déplace pas la racine");

    public string Label => $"Déplacer {node.Name}";

    public void Do() => newParent.AddChild(node);

    public void Undo() => _oldParent.AddChild(node);
}

public sealed class UndoStack
{
    readonly Stack<IEditorCommand> _undo = new();
    readonly Stack<IEditorCommand> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndo => _undo.TryPeek(out var c) ? c.Label : null;
    public string? NextRedo => _redo.TryPeek(out var c) ? c.Label : null;

    public void Execute(IEditorCommand command)
    {
        command.Do();
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.TryPop(out var c))
        {
            c.Undo();
            _redo.Push(c);
            Changed?.Invoke();
        }
    }

    public void Redo()
    {
        if (_redo.TryPop(out var c))
        {
            c.Do();
            _undo.Push(c);
            Changed?.Invoke();
        }
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
