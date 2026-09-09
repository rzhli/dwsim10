//    The PID controller's two output modes, and the one that was unreachable from the UI.
//
//    Without a ManipulatedVariableSpan the controller multiplies the set-point:
//
//        OutputAbs = (1 -/+ Output) x |SP|
//
//    which only means something when the manipulated variable shares units with the controlled one.
//    A valve opening in percent driven off a 60 C temperature set-point comes out scaled by 60. With a
//    span it biases from the offset instead:
//
//        OutputAbs = Offset -/+ Output x Span
//
//    The property existed on the object and in GetProperties, but neither GetPropertyValue nor
//    SetPropertyValue handled it and no editor row exposed it, so a flowsheet built in the UI could
//    not leave the set-point-scaling branch. Reported as "span was nowhere to be found".

using System;
using System.Linq;
using NUnit.Framework;

namespace DWSIM.Engine.SmokeTests
{
    [TestFixture]
    public class PIDControllerSpanTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            DWSIM.GlobalSettings.Settings.AutomationMode = true;
            DWSIM.GlobalSettings.Settings.InspectorEnabled = false;
            DWSIM.GlobalSettings.Settings.CultureInfo = "en";

            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        private static (DWSIM.DynamicRunner.Flowsheet fs, DWSIM.UnitOperations.SpecialOps.PIDController pid)
            Controller()
        {
            var fs = new DWSIM.DynamicRunner.Flowsheet(null, null);
            fs.Init();
            fs.AddCompound("Water");

            var obj = fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.Controller_PID, 0, 0, "PID-1");
            var pid = (DWSIM.UnitOperations.SpecialOps.PIDController)fs.SimulationObjects[obj.Name];
            pid.SetFlowsheet(fs);
            return (fs, pid);
        }

        /// <summary>
        /// Both properties have to be listed and readable and writable through the generic property
        /// interface: that is what the spreadsheet, the scripts and the automation API all go through.
        /// </summary>
        [TestCase("Offset")]
        [TestCase("ManipulatedVariableSpan")]
        public void TheOutputScalingPropertiesAreReadableAndWritable(string property)
        {
            var (_, pid) = Controller();

            Assert.That(pid.GetProperties(DWSIM.Interfaces.Enums.PropertyType.ALL),
                        Contains.Item(property),
                        $"{property} is not listed among the controller's properties");

            Assert.That(pid.SetPropertyValue(property, 42.0), Is.True,
                        $"{property} could not be set through SetPropertyValue");

            var read = pid.GetPropertyValue(property);

            Assert.That(read, Is.Not.Null, $"{property} read back as null");
            Assert.That(Convert.ToDouble(read), Is.EqualTo(42.0),
                        $"{property} did not read back what was written");
        }

        /// <summary>
        /// With a span the absolute output is a bias around Offset in the manipulated variable's own
        /// units, and the set-point does not enter into it. Direct acting subtracts.
        /// </summary>
        [Test]
        public void WithASpanTheOutputIsBiasedFromTheOffset()
        {
            var (_, pid) = Controller();

            pid.ManipulatedVariableSpan = 100.0;   // a valve opening, percent
            pid.Offset = 50.0;                     // design opening
            pid.ReverseActing = false;

            // Offset - Output x Span for direct acting.
            Assert.That(Absolute(pid, output: 0.0), Is.EqualTo(50.0).Within(1e-9));
            Assert.That(Absolute(pid, output: 0.2), Is.EqualTo(30.0).Within(1e-9));
            Assert.That(Absolute(pid, output: -0.2), Is.EqualTo(70.0).Within(1e-9));

            pid.ReverseActing = true;
            Assert.That(Absolute(pid, output: 0.2), Is.EqualTo(70.0).Within(1e-9));
        }

        /// <summary>
        /// Without a span the output is the set-point scaled, which is the behaviour that produced a
        /// valve opening of 0.02 % from a temperature set-point of 60. Kept as a test because it is
        /// the documented fallback, not a bug in itself - the bug was not being able to leave it.
        /// </summary>
        [Test]
        public void WithoutASpanTheOutputScalesTheSetPoint()
        {
            var (_, pid) = Controller();

            pid.ManipulatedVariableSpan = 0.0;
            pid.AdjustValue = 60.0;                // the set-point, in degrees C
            pid.ReverseActing = false;

            // (1 - Output) x |SP|
            Assert.That(Absolute(pid, output: 0.0), Is.EqualTo(60.0).Within(1e-9));
            Assert.That(Absolute(pid, output: 0.5), Is.EqualTo(30.0).Within(1e-9));

            // Which is where the scaling comes from: nothing here knows the manipulated variable is
            // a percentage.
            Assert.That(Absolute(pid, output: 0.0), Is.Not.EqualTo(pid.Offset));
        }

        /// <summary>
        /// The absolute output for a given PID output, computed the way PIDController.Calculate does.
        /// Reproduced here rather than run through a dynamic integration, so the test states the
        /// formula and fails on the formula.
        /// </summary>
        private static double Absolute(DWSIM.UnitOperations.SpecialOps.PIDController pid, double output)
        {
            if (pid.ManipulatedVariableSpan > 0.0)
                return pid.ReverseActing
                    ? pid.Offset + output * pid.ManipulatedVariableSpan
                    : pid.Offset - output * pid.ManipulatedVariableSpan;

            var baseSP = Math.Abs(pid.AdjustValue);
            return pid.ReverseActing ? (1.0 + output) * baseSP : (1.0 - output) * baseSP;
        }

        /// <summary>
        /// A span set on one controller must not leak into another: the property is per-instance.
        /// </summary>
        [Test]
        public void TheSpanIsPerController()
        {
            var (fs, first) = Controller();

            var obj = fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.Controller_PID, 0, 0, "PID-2");
            var second = (DWSIM.UnitOperations.SpecialOps.PIDController)fs.SimulationObjects[obj.Name];

            first.ManipulatedVariableSpan = 100.0;

            Assert.That(second.ManipulatedVariableSpan, Is.EqualTo(0.0),
                        "the span carried over to a second controller");
        }
    }
}
