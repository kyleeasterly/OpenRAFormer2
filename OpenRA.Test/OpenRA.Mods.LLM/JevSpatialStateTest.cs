#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.LLM;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class JevSpatialStateTest
	{
		[Test]
		public void RefineryDockRoutesCrossTransitOnlyApronButAvoidSolidFootprint()
		{
			var building = FieldLoader.Load<BuildingInfo>(new MiniYaml(null,
				MiniYaml.FromString("Dimensions: 3,4\nFootprint: _x_ xxx +++ ===\n", "refinery-test")));
			var solid = JevSpatialState.SolidTiles(building, CPos.Zero).ToHashSet();
			var terrain = Enumerable.Range(0, 5).SelectMany(x => Enumerable.Range(0, 5).Select(y => new CPos(x, y))).ToHashSet();
			var routes = JevSpatialState.Distances(new CPos(0, 2), terrain, solid);
			Assert.That(routes[new CPos(2, 2)], Is.EqualTo(2));
			Assert.That(routes.ContainsKey(new CPos(1, 1)), Is.False);
			Assert.That(routes[new CPos(0, 4)], Is.EqualTo(2));
		}

		[Test]
		public void RoutesCannotCrossUnexploredTerrainOrVisibleSolidObstacles()
		{
			var terrain = new HashSet<CPos> { new(0, 0), new(1, 0), new(2, 0), new(4, 0) };
			var routes = JevSpatialState.Distances(CPos.Zero, terrain, []);
			Assert.That(routes[new CPos(2, 0)], Is.EqualTo(2));
			Assert.That(routes.ContainsKey(new CPos(4, 0)), Is.False);
			Assert.That(JevSpatialState.Distances(CPos.Zero, terrain, [new(1, 0)]).ContainsKey(new CPos(2, 0)), Is.False);
			Assert.That(JevSpatialState.Distances(CPos.Zero, terrain, [CPos.Zero]), Is.Empty);
		}
	}
}
