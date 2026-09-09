using DWSIM.Drawing.SkiaSharp;
using DWSIM.Interfaces;
using DWSIM.UnitOperations.SpecialOps;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class FlowsheetSelectionTests
    {
        private static (DWSIM.DynamicRunner.Flowsheet fs, GraphicsSurface surface,
            ISimulationObject first, ISimulationObject second) Build()
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            var first = fs.AddObject(ObjectType.MaterialStream, 103, 107, "first");
            var second = fs.AddObject(ObjectType.MaterialStream, 303, 107, "second");
            var surface = (GraphicsSurface)fs.GetSurface();
            surface.Flowsheet = fs;
            surface.Zoom = 1;
            surface.SnapToGrid = true;
            surface.SelectedObjects[first.Name] = first.GraphicObject;
            surface.SelectedObjects[second.Name] = second.GraphicObject;
            surface.SelectedObject = second.GraphicObject;
            return (fs, surface, first, second);
        }

        private static void RightClick(GraphicsSurface surface, ISimulationObject target)
        {
            var graphic = target.GraphicObject;
            surface.InputContextMenu((int)(graphic.X + graphic.Width / 2),
                (int)(graphic.Y + graphic.Height / 2));
        }

        [Test]
        public void ContextMenuKeepsTheGroupAndDoesNotSnapObjects()
        {
            var (_, surface, first, second) = Build();
            var x = first.GraphicObject.X;
            var y = first.GraphicObject.Y;

            RightClick(surface, first);

            Assert.That(surface.SelectedObjects.Keys, Is.EquivalentTo(new[] { first.Name, second.Name }));
            Assert.That(surface.SelectedObject, Is.SameAs(first.GraphicObject));
            Assert.That(surface.dragging, Is.False, "opening a menu must not start a drag");
            Assert.That(first.GraphicObject.X, Is.EqualTo(x));
            Assert.That(first.GraphicObject.Y, Is.EqualTo(y));
        }

        [Test]
        public void ContextMenuOnBlankCanvasKeepsASelectionWithoutAPrimaryObject()
        {
            var (_, surface, first, second) = Build();
            surface.SelectedObject = null;

            surface.InputContextMenu(900, 900);

            Assert.That(surface.SelectedObjects.Keys, Is.EquivalentTo(new[] { first.Name, second.Name }));
            Assert.That(surface.SelectedObject, Is.Not.Null,
                "the next canvas repaint clears the group if there is no primary object");
        }

        [Test]
        public void ContextMenuOnAnUnselectedObjectReplacesTheGroup()
        {
            var (fs, surface, _, _) = Build();
            var other = fs.AddObject(ObjectType.MaterialStream, 503, 107, "other");

            RightClick(surface, other);

            Assert.That(surface.SelectedObjects.Keys, Is.EquivalentTo(new[] { other.Name }));
            Assert.That(surface.SelectedObject, Is.SameAs(other.GraphicObject));
        }

        [Test]
        public void BatchDeactivationUndoesAsOneChangeAndPreservesControllerEnableSettings()
        {
            var (fs, surface, first, second) = Build();
            second.GraphicObject.Active = false;
            var controller = (PIDController)fs.AddObject(ObjectType.Controller_PID, 503, 107, "PID");
            controller.Active = false;
            surface.SelectedObjects[controller.Name] = controller.GraphicObject;
            fs.FlowsheetOptions.EnabledUndoRedo = true;
            var undoCount = fs.UndoStack.Count;

            RightClick(surface, first);
            surface.SetSelectedObjectsActive(false);
            Assert.That(fs.UndoStack.Count, Is.EqualTo(undoCount + 1));
            Assert.That(first.GraphicObject.Active, Is.False);
            Assert.That(second.GraphicObject.Active, Is.False);
            Assert.That(controller.GraphicObject.Active, Is.False);

            fs.ProcessUndo();
            Assert.That(first.GraphicObject.Active, Is.True);
            Assert.That(second.GraphicObject.Active, Is.False, "restore the original mixed selection state");
            Assert.That(controller.GraphicObject.Active, Is.True);
            Assert.That(controller.Active, Is.False, "canvas activation must not enable an independently disabled PID");

            fs.ProcessRedo();
            Assert.That(first.GraphicObject.Active, Is.False);
            Assert.That(second.GraphicObject.Active, Is.False);
            Assert.That(controller.GraphicObject.Active, Is.False);
        }
    }
}
