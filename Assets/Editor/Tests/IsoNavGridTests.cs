/*
 * Copyright (c) 2026 jdseo921. All rights reserved.
 * This software and associated documentation files are proprietary and confidential.
 * Unauthorized copying, modification, or distribution of this file is strictly prohibited.
 * Written by jdseo921, jdseo0921@gmail.com
 */

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Edit-mode tests for the navigation core both levels share.
///
/// IsoNavGrid derives its heightmap from three tilemaps, so each test paints a
/// small arena from an ASCII map and calls Build() directly. Awake() is not
/// invoked in edit mode, which is what lets the maps be assigned first.
///
///   '.'  floor at z=0                      height 0.0
///   '#'  floor at z=0 with a collider      blocked
///   'r'  floor at z=0 plus a ramp tile     height 0.5
///   'P'  floor at z=1                      height 1.0
///   ' '  nothing painted                   not walkable
///
/// Rows read top to bottom, so row 0 is the highest y.
/// </summary>
public class IsoNavGridTests
{
    private GameObject root;

    [TearDown]
    public void TearDown()
    {
        if (root != null)
        {
            Object.DestroyImmediate(root);
            root = null;
        }
    }

    private IsoNavGrid BuildGrid(params string[] rows)
    {
        root = new GameObject("NavTestRoot");
        root.AddComponent<Grid>();

        Tilemap baseMap = AddMap("Base");
        Tilemap rampsMap = AddMap("Ramps");
        Tilemap collidersMap = AddMap("Colliders");
        Tile tile = ScriptableObject.CreateInstance<Tile>();

        int height = rows.Length;
        for (int row = 0; row < height; row++)
        {
            int y = height - 1 - row;
            string line = rows[row];
            for (int x = 0; x < line.Length; x++)
            {
                switch (line[x])
                {
                    case '.':
                        baseMap.SetTile(new Vector3Int(x, y, 0), tile);
                        break;
                    case '#':
                        baseMap.SetTile(new Vector3Int(x, y, 0), tile);
                        collidersMap.SetTile(new Vector3Int(x, y, 0), tile);
                        break;
                    case 'r':
                        baseMap.SetTile(new Vector3Int(x, y, 0), tile);
                        rampsMap.SetTile(new Vector3Int(x, y, 0), tile);
                        break;
                    case 'P':
                        baseMap.SetTile(new Vector3Int(x, y, 1), tile);
                        break;
                }
            }
        }

        IsoNavGrid nav = root.AddComponent<IsoNavGrid>();
        nav.baseMap = baseMap;
        nav.rampsMap = rampsMap;
        nav.collidersMap = collidersMap;
        nav.Build();
        return nav;
    }

    private Tilemap AddMap(string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(root.transform);
        return go.AddComponent<Tilemap>();
    }

    private static Vector3Int Cell(IsoNavGrid nav, int x, int y)
    {
        Vector3Int cell;
        Assert.IsTrue(nav.TryGetColumnCell(x, y, out cell), "column " + x + "," + y + " should exist");
        return cell;
    }

    // ---------------------------------------------------------------- walkability

    [Test]
    public void ColliderOnTopFloor_BlocksTheColumn()
    {
        IsoNavGrid nav = BuildGrid(
            "...",
            ".#.",
            "...");

        Assert.IsFalse(nav.ColumnWalkable(1, 1), "a collider on the top floor blocks its column");
        Assert.IsTrue(nav.ColumnWalkable(0, 1), "neighbouring floor stays walkable");
        Assert.AreEqual(8, nav.TotalWalkableColumns, "eight of the nine columns remain walkable");
    }

    [Test]
    public void UnpaintedColumn_IsNotWalkable()
    {
        IsoNavGrid nav = BuildGrid(
            "..",
            ". ");

        Assert.IsFalse(nav.ColumnWalkable(1, 0), "an unpainted column is not walkable");
    }

    // ---------------------------------------------------------------- height gating

    [Test]
    public void StepAllowed_GroundToPlatform_IsBlockedByMaxStep()
    {
        IsoNavGrid nav = BuildGrid(".P");

        Assert.AreEqual(0f, nav.ColumnHeightF(0, 0), 1e-4f);
        Assert.AreEqual(1f, nav.ColumnHeightF(1, 0), 1e-4f);
        Assert.IsFalse(nav.StepAllowed(0, 0, 1, 0),
            "a full level of height difference exceeds MaxStep " + IsoNavGrid.MaxStep);
    }

    [Test]
    public void StepAllowed_GroundToRamp_IsAllowed()
    {
        IsoNavGrid nav = BuildGrid(".r");

        Assert.AreEqual(0.5f, nav.ColumnHeightF(1, 0), 1e-4f, "a ramp sits half a level up");
        Assert.IsTrue(nav.StepAllowed(0, 0, 1, 0), "half a level is within MaxStep");
    }

    [Test]
    public void StepAllowed_RampToPlatform_IsAllowed()
    {
        IsoNavGrid nav = BuildGrid(".rP");

        Assert.IsTrue(nav.StepAllowed(1, 0, 2, 0),
            "a ramp is the bridge between ground and platform");
    }

    [Test]
    public void RampChain_MakesAPlatformReachable()
    {
        // The whole point of the ramp rule: a placed ramp is always climbable,
        // and without it the same platform is unreachable.
        IsoNavGrid withRamp = BuildGrid(".rP");
        Assert.IsTrue(withRamp.IsReachable(Cell(withRamp, 0, 0), Cell(withRamp, 2, 0)),
            "ground -> ramp -> platform is a route");

        TearDown();

        IsoNavGrid withoutRamp = BuildGrid("..P");
        Assert.IsFalse(withoutRamp.IsReachable(Cell(withoutRamp, 0, 0), Cell(withoutRamp, 2, 0)),
            "the same platform without a ramp is a cliff");
    }

    // ---------------------------------------------------------------- pathfinding

    [Test]
    public void FindPath_OnOpenGround_Succeeds()
    {
        IsoNavGrid nav = BuildGrid(
            ".....",
            ".....",
            ".....");

        List<Vector3Int> path = new List<Vector3Int>();
        Assert.IsTrue(nav.FindPath(Cell(nav, 0, 0), Cell(nav, 4, 2), path));
        Assert.Greater(path.Count, 0, "a successful path is not empty");
        Assert.AreEqual(Cell(nav, 4, 2), path[path.Count - 1], "the path ends on the goal");
    }

    [Test]
    public void FindPath_ThroughASolidWall_Fails()
    {
        IsoNavGrid nav = BuildGrid(
            "..#..",
            "..#..",
            "..#..");

        List<Vector3Int> path = new List<Vector3Int>();
        Assert.IsFalse(nav.FindPath(Cell(nav, 0, 1), Cell(nav, 4, 1), path),
            "a wall with no opening has no route through it");
    }

    [Test]
    public void FindPath_ThroughADoorway_UsesIt()
    {
        IsoNavGrid nav = BuildGrid(
            "..#..",
            ".....",
            "..#..");

        List<Vector3Int> path = new List<Vector3Int>();
        Assert.IsTrue(nav.FindPath(Cell(nav, 0, 1), Cell(nav, 4, 1), path),
            "the gap in the wall is a route");
        Assert.Contains(Cell(nav, 2, 1), path, "the route goes through the doorway column");
    }

    [Test]
    public void FindPath_RecordsMetrics()
    {
        // The HUD reports these, so they are part of the contract.
        IsoNavGrid nav = BuildGrid("...", "...");
        List<Vector3Int> path = new List<Vector3Int>();

        nav.FindPath(Cell(nav, 0, 0), Cell(nav, 2, 1), path);

        Assert.AreEqual(1, nav.PathsRequested);
        Assert.AreEqual(1, nav.PathsSucceeded);
        Assert.AreEqual(0, nav.PathsFailed);
    }

    // ---------------------------------------------------------------- chokepoints

    [Test]
    public void Doorway_IsAChokepoint()
    {
        IsoNavGrid nav = BuildGrid(
            "..#..",
            ".....",
            "..#..");

        Assert.IsTrue(nav.IsChokepoint(2, 1), "a one-cell gap in a wall is a chokepoint");
    }

    [Test]
    public void OpenGround_IsNotAChokepoint()
    {
        IsoNavGrid nav = BuildGrid(
            ".....",
            ".....",
            ".....",
            ".....",
            ".....");

        Assert.IsFalse(nav.IsChokepoint(2, 2), "the middle of an open field is not a chokepoint");
    }

    // ---------------------------------------------------------------- line of sight

    [Test]
    public void HasLineOfSight_AcrossOpenGround_IsClear()
    {
        IsoNavGrid nav = BuildGrid(
            ".....",
            ".....",
            ".....");

        Assert.IsTrue(nav.HasLineOfSight(Cell(nav, 0, 1), Cell(nav, 4, 1)));
    }

    [Test]
    public void HasLineOfSight_ThroughAWall_IsBlocked()
    {
        IsoNavGrid nav = BuildGrid(
            "..#..",
            "..#..",
            "..#..");

        Assert.IsFalse(nav.HasLineOfSight(Cell(nav, 0, 1), Cell(nav, 4, 1)),
            "sight does not pass through a blocked column");
    }

    // ---------------------------------------------------------------- flow field

    [Test]
    public void FlowField_StepsLeadToTheGoal()
    {
        IsoNavGrid nav = BuildGrid(
            ".....",
            ".....",
            ".....");

        Vector3Int goal = Cell(nav, 4, 2);
        nav.ComputeFlowField(goal);
        Assert.IsTrue(nav.HasFlowField);

        // Following the field from the far corner must arrive, which is what lets
        // the horde share one search instead of running A* per zombie.
        Vector3Int at = Cell(nav, 0, 0);
        for (int step = 0; step < 64 && at != goal; step++)
        {
            Vector3Int next;
            Assert.IsTrue(nav.FlowNextStep(at, out next), "the field gives a step from " + at);
            Assert.AreNotEqual(at, next, "the field must advance, not stall");
            at = next;
        }

        Assert.AreEqual(goal, at, "following the flow field reaches the goal");
    }

    [Test]
    public void CountReachableFrom_IgnoresWalledOffPockets()
    {
        // The builder's auto-repair pass depends on this to notice sealed rooms.
        IsoNavGrid nav = BuildGrid(
            "..###",
            "..#..",
            "..###");

        int reachable = nav.CountReachableFrom(Cell(nav, 0, 1));

        // The open left side is the 2x3 block at x0-x1; the pocket is x3-x4 at y1,
        // sealed off by the x2 wall column.
        Assert.AreEqual(8, nav.TotalWalkableColumns, "six open columns plus the two-column pocket");
        Assert.AreEqual(6, reachable, "the pocket behind the wall is not reachable");
    }
}
#endif
