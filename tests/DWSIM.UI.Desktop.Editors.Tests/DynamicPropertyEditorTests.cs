//    The dynamic-mode parameters on a unit operation editor.
//
//    An editor built on UnitOpEditor hands the host a FullContent, and ObjectEditorContainer shows
//    that in place of its own tab strip - including the Dynamics tab. Every unit operation with a
//    laid-out editor therefore had no reachable dynamic parameters: a heat exchanger's Number of
//    Cells and Wall Thermal Mass existed on the object and were saved to the file, but there was
//    nowhere in the UI to see or change them. Reported against HX.dwxmz.
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
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using DWSIM.Interfaces;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;

namespace DWSIM.UI.Desktop.Editors.Tests
{
    [TestFixture]
    public class DynamicPropertyEditorTests
    {
        private IFlowsheet _flowsheet;

        [OneTimeSetUp]
        public void SetUpOnce()
        {
            GlobalSettings.Settings.AutomationMode = true;
            GlobalSettings.Settings.CultureInfo = "en";
            FlowsheetBase.FlowsheetBase.AddPropPacks();
        }

        [SetUp]
        public void SetUp()
        {
            _flowsheet = (IFlowsheet)new DWSIM.Automation.Automation3().CreateFlowsheet();
        }

        private ISimulationObject Add(ObjectType type, string tag)
        {
            var obj = _flowsheet.AddObject(type, 0, 0, tag);
            return _flowsheet.SimulationObjects[obj.Name];
        }

        private static IEnumerable<Control> Descendants(Control root)
        {
            yield return root;

            IEnumerable<Control> children = root switch
            {
                Panel p => p.Children.OfType<Control>(),
                ContentControl c when c.Content is Control inner => new[] { inner },
                Decorator d when d.Child is Control child => new[] { child },
                _ => Array.Empty<Control>()
            };

            foreach (var child in children)
                foreach (var item in Descendants(child))
                    yield return item;
        }

        /// <summary>Every string the editor puts on screen, labels and values alike.</summary>
        private static List<string> TextOf(Control editor)
        {
            var texts = Descendants(editor).OfType<TextBlock>()
                                           .Select(t => t.Text)
                                           .Where(t => !string.IsNullOrEmpty(t))
                                           .ToList();
            texts.AddRange(Descendants(editor).OfType<TextBox>()
                                              .Select(t => t.Text)
                                              .Where(t => !string.IsNullOrEmpty(t)));
            return texts;
        }

        /// <summary>
        /// Types a value into a row, the way a user does. Avalonia raises TextChanged from the
        /// control's own input handling, not from the property setter, so a TextBox that was never
        /// attached to a visual tree stays silent when Text is assigned; the editors commit on that
        /// event, so the test has to raise it.
        /// </summary>
        private static void Type(TextBox box, string text)
        {
            box.Text = text;
            box.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent));
        }

        /// <summary>
        /// The editable control of the row labelled <paramref name="label"/>. The rows are a two-column
        /// grid of a TextBlock and the control, so pairing them is how a reader tells which box is which.
        /// </summary>
        private static TextBox RowBox(Control editor, string label)
        {
            foreach (var grid in Descendants(editor).OfType<Grid>())
            {
                var caption = grid.Children.OfType<TextBlock>().FirstOrDefault();
                if (caption?.Text == null) continue;
                if (!caption.Text.StartsWith(label, StringComparison.Ordinal)) continue;

                var box = grid.Children.OfType<TextBox>().FirstOrDefault();
                if (box != null) return box;
            }
            return null;
        }

        /// <summary>
        /// The two the report named. They are dynamic properties of the exchanger, so they only
        /// appear if the editor renders that collection at all.
        /// </summary>
        [TestCase("Number of Cells")]
        [TestCase("Wall Thermal Mass")]
        [TestCase("Volume for Cold Fluid")]
        [TestCase("Volume for Hot Fluid")]
        [TestCase("Minimum Pressure")]
        [TestCase("Substeps")]
        public void TheHeatExchangerEditorShowsItsDynamicParameters(string parameter)
        {
            var hx = Add(ObjectType.HeatExchanger, "HX-1");
            var editor = HeatExchangerEditor.Build(
                (DWSIM.UnitOperations.UnitOperations.HeatExchanger)hx);

            var texts = TextOf(editor);

            Assert.That(texts.Any(t => t.StartsWith(parameter, StringComparison.Ordinal)), Is.True,
                        $"'{parameter}' is nowhere in the heat exchanger editor. " +
                        $"Shown: {string.Join(" | ", texts.Take(40))}");
        }

        /// <summary>
        /// The group has to be there for the other laid-out editors too, not just the exchanger:
        /// they all go through the same frame and all lost the tab the same way.
        /// </summary>
        [Test]
        public void TheOtherLaidOutEditorsShowTheirDynamicParametersToo()
        {
            var cases = new (ObjectType Type, string Tag, Func<ISimulationObject, Control> Build, string Parameter)[]
            {
                (ObjectType.Valve, "V-1",
                    o => ValveEditor.Build((DWSIM.UnitOperations.UnitOperations.Valve)o),
                    "Delta-P Calculation Delay"),
                (ObjectType.Pump, "P-1",
                    o => PumpEditor.Build((DWSIM.UnitOperations.UnitOperations.Pump)o),
                    "Volume"),
                (ObjectType.Heater, "H-1",
                    o => HeaterCoolerEditor.Build((DWSIM.UnitOperations.UnitOperations.Heater)o),
                    "Volume"),
                (ObjectType.Tank, "T-1",
                    o => TankEditor.Build((DWSIM.UnitOperations.UnitOperations.Tank)o),
                    "Volume")
            };

            foreach (var c in cases)
            {
                var obj = Add(c.Type, c.Tag);
                if (!obj.SupportsDynamicMode) continue;

                var texts = TextOf(c.Build(obj));

                Assert.That(texts.Any(t => t.Contains("Dynamics", StringComparison.Ordinal)), Is.True,
                            $"the {c.Type} editor has no Dynamics group");
            }
        }

        /// <summary>
        /// The values shown are the object's own, and editing a row writes back to it. Number of
        /// Cells is the one that matters here: the dynamic model rebuilds its cell list from it.
        /// </summary>
        [Test]
        public void EditingADynamicParameterWritesItBackToTheObject()
        {
            var hx = (DWSIM.UnitOperations.UnitOperations.HeatExchanger)Add(ObjectType.HeatExchanger, "HX-1");

            hx.SetDynamicProperty("Number of Cells", 4.0);

            var editor = HeatExchangerEditor.Build(hx);

            var cellBox = RowBox(editor, "Number of Cells");

            Assert.That(cellBox, Is.Not.Null, "there is no Number of Cells row in the editor");
            Assert.That(cellBox.Text, Is.EqualTo("4"),
                        "the row is not showing the value the exchanger holds");

            Type(cellBox, "6");

            Assert.That(Convert.ToDouble(hx.GetDynamicProperty("Number of Cells")), Is.EqualTo(6.0),
                        "editing the row did not write the value back to the exchanger");
        }

        /// <summary>
        /// An object that does not support dynamic mode gets no group, rather than an empty one.
        /// </summary>
        [Test]
        public void AnObjectWithoutDynamicModeGetsNoGroup()
        {
            var adjust = Add(ObjectType.OT_Adjust, "ADJ-1");

            Assume.That(adjust.SupportsDynamicMode, Is.False,
                        "this test needs an object that does not support dynamic mode");

            var editor = AdjustEditor.Build((DWSIM.UnitOperations.SpecialOps.Adjust)adjust);

            Assert.That(TextOf(editor).Any(t => t.Contains("Dynamics", StringComparison.Ordinal)), Is.False,
                        "an object with no dynamic mode should not get a Dynamics group");
        }
    }
}
