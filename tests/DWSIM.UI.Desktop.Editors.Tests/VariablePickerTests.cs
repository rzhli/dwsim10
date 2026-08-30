//    The variable a controller points at, as the Linked Objects tab presents it.
//
//    The rows carry three pieces of state that have to stay consistent with each other: the object,
//    one of its properties and the unit the value is read in. HX.dwxmz showed what happens when they
//    drift apart - its PID was controlling a stream temperature in "C", and switching the link to a
//    valve kept "C" while the property became a dimensionless opening, so Current Value read
//    -273.15. The property codes were also shown raw ("PROP_VA_5"), which is unreadable.
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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.UI.Desktop.Editors;
using DWSIM.UI.Shared.Avalonia;
using NUnit.Framework;
using ObjectType = DWSIM.Interfaces.Enums.GraphicObjects.ObjectType;
using PIDController = DWSIM.UnitOperations.SpecialOps.PIDController;
using SpecialOpObjectInfo = DWSIM.UnitOperations.SpecialOps.Helpers.SpecialOpObjectInfo;

namespace DWSIM.UI.Desktop.Editors.Tests
{
    [TestFixture]
    public class VariablePickerTests
    {
        private IFlowsheet _flowsheet;
        private PIDController _pid;
        private SpecialOpObjectInfo _info;

        /// <summary>The pickers of one variable row, in the order the panel adds them.</summary>
        private sealed class Row
        {
            public ComboBox Object, Property, UnitsGroup, Units;
            public TextBlock Value;

            public List<string> PropertyOptions =>
                ((IEnumerable)Property.ItemsSource).Cast<string>().ToList();

            /// <summary>Chooses a property by the name shown, as a reader of the list would.</summary>
            public void PickProperty(string shownName)
            {
                var index = PropertyOptions.FindIndex(x => x == shownName);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), "no property is listed as " + shownName);
                Property.SelectedIndex = index;
            }
        }

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

            _flowsheet.AddObject(ObjectType.MaterialStream, 0, 0, "3");
            _flowsheet.AddObject(ObjectType.Valve, 100, 0, "VALVE-1");

            var pidObj = _flowsheet.AddObject(ObjectType.Controller_PID, 200, 0, "PID-1");
            _pid = (PIDController)_flowsheet.SimulationObjects[pidObj.Name];

            // the state HX.dwxmz was saved in: a stream temperature, read in degrees Celsius
            _info = (SpecialOpObjectInfo)_pid.ControlledObjectData;
            _info.ID = _flowsheet.GetFlowsheetSimulationObject("3").Name;
            _info.Name = "3";
            _info.PropertyName = "PROP_MS_0";
            _info.Units = "C";
            _info.UnitsType = UnitOfMeasure.temperature;
        }

        private Row Build(bool writable)
        {
            var panel = new AvaloniaEditorPanel();

            VariablePicker.Add(panel, _pid, _info, VariablePicker.Role.AdjustControlled,
                               "Controlled", writable: writable, withUnits: true);

            var children = Descendants(panel).ToList();
            var combos = children.OfType<ComboBox>().ToList();

            return new Row
            {
                Object = combos[0],
                Property = combos[1],
                UnitsGroup = combos[2],
                Units = combos[3],
                Value = children.OfType<TextBlock>().Last(x => x.Text != null)
            };
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

        [Test]
        public void ThePropertiesAreListedByNameAndNotByCode()
        {
            var row = Build(writable: false);

            Assert.That(row.PropertyOptions, Has.No.Member("PROP_MS_0"), "a code tells the reader nothing");
            Assert.That(row.PropertyOptions, Has.Member("Temperature (C)"));
            Assert.That(row.Property.SelectedItem, Is.EqualTo("Temperature (C)"),
                        "the saved property is the one shown");
        }

        [Test]
        public void TheSavedLinkIsShownAsItWasStored()
        {
            var row = Build(writable: false);

            Assert.That(row.Object.SelectedItem, Is.EqualTo("3"));
            Assert.That(row.UnitsGroup.SelectedItem, Is.EqualTo("temperature"));
            Assert.That(row.Units.SelectedItem, Is.EqualTo("C"));
            Assert.That(row.Value.Text, Does.Contain("C"));
        }

        /// <summary>
        /// The reported reading of -273.15: a dimensionless valve opening converted out of Kelvin.
        /// </summary>
        [Test]
        public void MovingToADimensionlessPropertyDropsTheTemperatureUnit()
        {
            var row = Build(writable: true);

            row.Object.SelectedItem = "VALVE-1";
            row.PickProperty("Opening");

            Assert.That(_info.PropertyName, Is.EqualTo("PROP_VA_5"));
            Assert.That(_info.Units, Is.Empty, "an opening is a percentage, not a temperature");
            Assert.That(row.Value.Text, Does.Not.Contain("-273"));
            Assert.That(row.Value.Text.Trim(), Is.EqualTo("50"), "a new valve sits half open");
        }

        [Test]
        public void TheUnitPickersFollowTheNewProperty()
        {
            var row = Build(writable: true);

            row.Object.SelectedItem = "VALVE-1";
            row.PickProperty("Outlet Pressure (bar)");

            Assert.That(_info.UnitsType, Is.EqualTo(UnitOfMeasure.pressure));
            Assert.That(row.UnitsGroup.SelectedItem, Is.EqualTo("pressure"));
            Assert.That(row.Units.SelectedItem, Is.EqualTo("bar"));
        }

        /// <summary>A unit the user chose within the right dimension is theirs to keep.</summary>
        [Test]
        public void AUnitOfTheSameDimensionIsLeftAlone()
        {
            _info.PropertyName = "PROP_MS_2";
            _info.Units = "kg/h";
            _info.UnitsType = UnitOfMeasure.massflow;

            var row = Build(writable: true);
            row.PickProperty("Molar Flow (kmol/h)");

            Assert.That(_info.Units, Is.EqualTo("kg/h").Or.EqualTo("kmol/h"));
            Assert.That(row.Value.Text, Does.Not.Contain("NaN"));
        }

        [Test]
        public void SwitchingObjectsDoesNotThrow()
        {
            var row = Build(writable: true);

            Assert.DoesNotThrow(() =>
            {
                row.Object.SelectedItem = "VALVE-1";
                row.Object.SelectedItem = "3";
                row.Object.SelectedItem = "VALVE-1";
            }, "this used to take the process down on the first switch");

            Assert.That(row.PropertyOptions, Has.Member("Opening"));
        }

        /// <summary>
        /// HX.dwxmz stored a manipulated ID that resolved to nothing. The picker cannot show it, so
        /// it says so instead of quietly displaying the first object on the flowsheet.
        /// </summary>
        [Test]
        public void AnUnresolvedLinkIsCalledOut()
        {
            _info.ID = "VALV-e8d38c56-d920-420d-b2ad-d310d5fb0c02";
            _info.Name = "VALVE-1";

            var panel = new AvaloniaEditorPanel();
            VariablePicker.Add(panel, _pid, _info, VariablePicker.Role.AdjustControlled,
                               "Controlled", writable: true, withUnits: true);

            var text = string.Join(" ", Descendants(panel).OfType<TextBlock>()
                                                          .Select(x => x.Text)
                                                          .Where(x => x != null));

            Assert.That(text, Does.Contain("not on the flowsheet anymore"));
            Assert.That(text, Does.Contain("VALVE-1"));
        }
    }
}
