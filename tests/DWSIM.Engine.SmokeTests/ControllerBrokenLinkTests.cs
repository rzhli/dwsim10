//    A controller whose manipulated or controlled object is not on the flowsheet.
//
//    The link is stored as an object ID, and that ID can stop resolving: the object was deleted
//    after the controller was pointed at it, or the file was saved by a host that renumbered the
//    IDs (a simulation cloned from another one keeps the GUIDs of the original). UpdateVars
//    dereferenced the result of SingleOrDefault without checking it, and the PID graphic calls
//    UpdateVars from its Draw - so a repaint of the flowsheet threw NullReferenceException out of
//    the paint handler, where nothing catches, and the process went away.
//
//    This file is part of DWSIM.
//
//    DWSIM is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.
//
//    DWSIM is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with DWSIM.  If not, see <http://www.gnu.org/licenses/>.

using System;
using NUnit.Framework;
using DWSIM.Interfaces.Enums.GraphicObjects;
using PIDController = DWSIM.UnitOperations.SpecialOps.PIDController;
using SpecialOpObjectInfo = DWSIM.UnitOperations.SpecialOps.Helpers.SpecialOpObjectInfo;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class ControllerBrokenLinkTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            DWSIM.GlobalSettings.Settings.AutomationMode = true;
            DWSIM.GlobalSettings.Settings.InspectorEnabled = false;
            DWSIM.GlobalSettings.Settings.CultureInfo = "en";

            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        /// <summary>
        /// A flowsheet with one valve, one material stream and a PID pointed at both, then the
        /// caller breaks whichever link the test is about by storing an ID that resolves to nothing.
        /// </summary>
        private static (DWSIM.DynamicRunner.Flowsheet flowsheet, PIDController pid) Build()
        {
            var flowsheet = new DWSIM.DynamicRunner.Flowsheet(null, null);
            flowsheet.Init();
            flowsheet.AddCompound("Water");

            var pp = new DWSIM.Thermodynamics.PropertyPackages.SteamTablesPropertyPackage
            {
                Flowsheet = flowsheet
            };
            flowsheet.AddPropertyPackage(pp);

            var streamObj = flowsheet.AddObject(ObjectType.MaterialStream, 0, 0, "feed");
            var stream = (DWSIM.Thermodynamics.Streams.MaterialStream)flowsheet.SimulationObjects[streamObj.Name];
            stream.SetFlowsheet(flowsheet);
            stream.SetPropertyPackage(pp);
            stream.SetMassFlow(10.0);

            var valveObj = flowsheet.AddObject(ObjectType.Valve, 100, 0, "VALVE-1");

            var pidObj = flowsheet.AddObject(ObjectType.Controller_PID, 200, 0, "PID-1");
            var pid = (PIDController)flowsheet.SimulationObjects[pidObj.Name];

            var manipulated = (SpecialOpObjectInfo)pid.ManipulatedObjectData;
            manipulated.ID = valveObj.Name;
            manipulated.Name = "VALVE-1";
            manipulated.PropertyName = "PROP_VA_5";      // opening
            manipulated.Units = "";

            var controlled = (SpecialOpObjectInfo)pid.ControlledObjectData;
            controlled.ID = streamObj.Name;
            controlled.Name = "feed";
            controlled.PropertyName = "PROP_MS_2";       // mass flow
            controlled.Units = "kg/s";

            pid.AdjustValue = 10.0;

            return (flowsheet, pid);
        }

        [Test]
        public void UpdateVarsSurvivesAManipulatedObjectThatIsGone()
        {
            var (_, pid) = Build();
            ((SpecialOpObjectInfo)pid.ManipulatedObjectData).ID = "VALV-does-not-exist";

            Assert.DoesNotThrow(() => pid.UpdateVars(),
                "the PID graphic calls this from Draw, so a throw here takes the UI down");
        }

        [Test]
        public void UpdateVarsSurvivesAControlledObjectThatIsGone()
        {
            var (_, pid) = Build();
            ((SpecialOpObjectInfo)pid.ControlledObjectData).ID = "MAT-does-not-exist";

            Assert.DoesNotThrow(() => pid.UpdateVars());
        }

        /// <summary>
        /// The reported crash: the file's controller carried a manipulated ID that no longer
        /// resolved, and choosing an object in the editor repainted the canvas, which repaints the
        /// PID graphic, which calls UpdateVars.
        /// </summary>
        [Test]
        public void TheGraphicRepaintsWithABrokenLink()
        {
            var (_, pid) = Build();
            ((SpecialOpObjectInfo)pid.ManipulatedObjectData).ID = "VALV-does-not-exist";

            using var bitmap = new SkiaSharp.SKBitmap(400, 300);
            using var canvas = new SkiaSharp.SKCanvas(bitmap);

            Assert.DoesNotThrow(() => pid.GraphicObject.Draw(canvas));
        }

        [Test]
        public void UpdateVarsKeepsResolvingBothObjectsWhenTheyExist()
        {
            var (_, pid) = Build();

            pid.UpdateVars();

            Assert.That(pid.ManipulatedObject, Is.Not.Null, "Calculate writes the new MV through this");
            Assert.That(pid.ControlledObject, Is.Not.Null);
            Assert.That(pid.SPValue, Is.EqualTo(10.0).Within(1e-9));
            Assert.That(pid.PVValue, Is.EqualTo(10.0).Within(1e-6), "the feed carries 10 kg/s");
        }

        /// <summary>A bad link is a configuration error the solver can report per object, not a
        /// NullReferenceException from the depths of Calculate.</summary>
        [Test]
        public void CalculateReportsTheBrokenLink()
        {
            var (_, pid) = Build();
            ((SpecialOpObjectInfo)pid.ManipulatedObjectData).ID = "VALV-does-not-exist";

            var ex = Assert.Throws<Exception>(() => pid.Calculate());
            Assert.That(ex.Message, Does.Contain("VALVE-1"));
            Assert.That(ex.Message, Does.Contain("not on the flowsheet"));
        }
    }
}
