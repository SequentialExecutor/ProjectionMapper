using System;

namespace ProjectionMapper.Services
{
    public sealed class ArtworkAssignmentAction(string description, Action undo, Action redo) : IUndoRedoAction
    {
        public string Description => description;
        public void Undo() => undo();
        public void Redo() => redo();
    }
}
