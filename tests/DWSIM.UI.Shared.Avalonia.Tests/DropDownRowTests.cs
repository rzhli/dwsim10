//    The pickers the editor panels build, and the one rule for refilling them.
//
//    Avalonia's ItemsControl takes either the inline Items collection or a bound ItemsSource and
//    throws if a caller mixes them. The CreateAndAddDropDownRow helpers used to fill Items, while
//    every refill in the editors assigned ItemsSource - so choosing a different object in a
//    controller's or logical block's picker threw InvalidOperationException("Items collection must
//    be empty before using ItemsSource") from inside a SelectionChanged handler, where nothing
//    catches, and the application disappeared.
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

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DWSIM.UI.Shared.Avalonia;
using NUnit.Framework;

namespace DWSIM.UI.Shared.Avalonia.Tests
{
    [TestFixture]
    public class DropDownRowTests
    {
        private static List<string> OptionsOf(global::Avalonia.Controls.ComboBox cb) =>
            ((IEnumerable)cb.ItemsSource!).Cast<string>().ToList();

        [Test]
        public void ARowCarriesItsOptionsAndSelection()
        {
            var panel = new AvaloniaEditorPanel();

            var cb = panel.CreateAndAddDropDownRow("Property",
                new List<string> { "PROP_MS_0", "PROP_MS_1", "PROP_MS_2" }, 1, null);

            Assert.That(OptionsOf(cb), Has.Count.EqualTo(3));
            Assert.That(cb.SelectedItem, Is.EqualTo("PROP_MS_1"));
        }

        /// <summary>
        /// The reported crash: the PID editor's Controlled Object picker is switched to a valve, so
        /// the property picker next to it is refilled with the valve's properties.
        /// </summary>
        [Test]
        public void ARowCanBeRefilledAfterItWasBuilt()
        {
            var panel = new AvaloniaEditorPanel();

            var cb = panel.CreateAndAddDropDownRow("Controlled Property",
                new List<string> { "PROP_MS_0", "PROP_MS_1" }, 0, null);

            Assert.DoesNotThrow(() => cb.SetOptions(new List<string> { "PROP_VA_0", "PROP_VA_5" }),
                "mixing Items and ItemsSource throws, and this runs inside SelectionChanged");

            Assert.That(OptionsOf(cb), Is.EqualTo(new[] { "PROP_VA_0", "PROP_VA_5" }));
        }

        [Test]
        public void ARowSurvivesRepeatedRefills()
        {
            var panel = new AvaloniaEditorPanel();
            var cb = panel.CreateAndAddDropDownRow("Units", new List<string> { "C", "K" }, 0, null);

            for (var i = 0; i < 5; i++)
                cb.SetOptions(new List<string> { "kg/s", "kg/h", "t/h" });

            Assert.That(OptionsOf(cb), Has.Count.EqualTo(3));
        }

        [Test]
        public void RefillingLeavesSelectionToTheCaller()
        {
            var panel = new AvaloniaEditorPanel();
            var cb = panel.CreateAndAddDropDownRow("Units", new List<string> { "C", "K" }, 1, null);

            cb.SetOptions(new List<string> { "bar", "atm", "psi" });
            cb.SelectedIndex = 2;

            Assert.That(cb.SelectedItem, Is.EqualTo("psi"));
        }

        [Test]
        public void AnEmptyRefillClearsTheOptions()
        {
            var panel = new AvaloniaEditorPanel();
            var cb = panel.CreateAndAddDropDownRow("Chart", new List<string> { "a", "b" }, 0, null);

            cb.SetOptions(new List<string>());

            Assert.That(OptionsOf(cb), Is.Empty);
            Assert.That(cb.SelectedIndex, Is.LessThan(0));
        }

        /// <summary>The overload that selects by value, used where the caller has the item and not its index.</summary>
        [Test]
        public void TheSelectByValueOverloadAlsoRefills()
        {
            var panel = new AvaloniaEditorPanel();

            var cb = panel.CreateAndAddDropDownRow("PID Form",
                new List<string> { "Parallel", "ISA (Standard)", "Series" }, "Series", null);

            Assert.That(cb.SelectedItem, Is.EqualTo("Series"));
            Assert.DoesNotThrow(() => cb.SetOptions(new List<string> { "Parallel" }));
        }

        [Test]
        public void TheFixedWidthOverloadAlsoRefills()
        {
            var panel = new AvaloniaEditorPanel();

            var cb = panel.CreateAndAddDropDownRow("Object",
                new List<string> { "feed", "VALVE-1" }, 0, null, 240);

            Assert.That(cb.Width, Is.EqualTo(240));
            Assert.DoesNotThrow(() => cb.SetOptions(new List<string> { "HX-2" }));
        }
    }
}
