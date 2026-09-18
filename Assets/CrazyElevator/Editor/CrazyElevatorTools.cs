using System;
using System.IO;
using CrazyElevator;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class CrazyElevatorTools
{
    [InitializeOnLoadMethod]
    static void VerifyOnImport()
    {
        EditorApplication.delayCall += () =>
        {
            EnsureRuntimeMaterial();
            if (EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool("CrazyElevator.Checked.v1", false)) return;
            RunChecks(); SessionState.SetBool("CrazyElevator.Checked.v1", true);
        };
    }

    static void EnsureRuntimeMaterial()
    {
        const string folder = "Assets/CrazyElevator/Resources";
        const string path = folder + "/CrazyElevatorLit.mat";
        if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
        Directory.CreateDirectory(folder);
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) return;
        var material = new Material(shader) { name = "CrazyElevatorLit" };
        AssetDatabase.CreateAsset(material, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("CRAZY_ELEVATOR_MATERIAL_PASS: created Resources/CrazyElevatorLit.mat for player shader retention.");
    }

    static void Expect(bool value, string message)
    { if (!value) throw new Exception("Crazy Elevator validation failed: " + message); }

    [MenuItem("Crazy Elevator/Validate Game Rules")]
    public static void RunChecks()
    {
        var r = new ElevatorRound();
        Expect(r.Riders.Count == ElevatorRound.Floors * 6 && r.Load == 0 && !r.Finished, "fresh six-type round");
        var courier = r.Riders[0]; var pregnant = r.Riders[1]; var interview = r.Riders[2];
        var boss = r.Riders[3]; var elderly = r.Riders[4]; var group = r.Riders[5];
        Expect(courier.Space == 2 && courier.Destination == 1, "courier is a two-space short trip");
        Expect(pregnant.Space == 2 && group.Space == 3, "pregnant and group footprint sizes");
        Expect(interview.Patience == 13 && elderly.Arrival > 6, "urgent interview and slow elderly arrival");
        Expect(!r.Board(r.Riders[6]), "cannot board a passenger from another floor");
        Expect(!r.Board(elderly), "slow passenger must reach the door");
        Expect(r.Board(courier) && r.Board(interview) && r.Board(boss) && r.Load == 4, "mixed parties board");
        Expect(!r.Board(courier) && r.Board(group) && r.Load == 7, "duplicate blocked while physical placement owns fit");
        r.HoldDoor(.6f); Expect(!boss.HoldSatisfied, "boss bonus needs a real hold");
        r.HoldDoor(.7f); Expect(boss.HoldSatisfied, "boss bonus earned by holding open");
        Expect(r.Arrive(1) == 2 && r.Load == 7 && r.Happy == 0, "arrival waits for manual offboarding");
        Expect(r.Offboard(courier) == OffboardResult.Happy && r.Load == 5 && r.Happy == 1, "manual correct-floor exit");
        Expect(r.Offboard(boss) == OffboardResult.WrongFloor && boss.Mood == 0 && r.Load == 4, "wrong-floor exit is unhappy");

        r = new ElevatorRound(); courier = r.Riders[0];
        Expect(r.Board(courier), "board courier for missed-stop test");
        r.Arrive(1);
        int previous = r.Score;
        Expect(r.LeaveFloor() == 1 && courier.Mood == 1 && r.Score == previous - 35, "passing a requested floor lowers mood");
        r = new ElevatorRound(); elderly = r.Riders[4];
        Expect(r.Reject(r.Riders[0]) && r.Reject(r.Riders[1]), "queue advances after passing parties");
        r.Tick(6.3f, true); Expect(r.Board(elderly), "holding the floor lets the elderly rider arrive");
        previous = r.Score; Expect(r.Remove(elderly) && r.Score == previous - 40, "eject penalty");

        r = new ElevatorRound(); r.Tick(14, true);
        Expect(r.Riders[2].Resolved && r.Missed == 1, "urgent waiting rider expires");
        Expect(r.Riders[6].Remaining == r.Riders[6].Patience, "unvisited floors do not drain");
        r.Tick(200, false);
        Expect(r.Finished && r.TimeLeft == 0 && !r.Board(r.Riders[1]), "timer clamps and input closes");
        r = new ElevatorRound();
        // Short courier trips prove that manual routing and happy delivery
        // still work without an artificial passenger quota.
        for (int stop = 0; stop < 2 && !r.Finished; stop++)
        {
            CrazyElevator.Rider routeCourier = null;
            foreach (var p in r.Riders)
            {
                if (!p.Resolved && !p.Boarded && p.Origin == r.Floor && p.Kind == "COURIER")
                {
                    routeCourier = p;
                    break;
                }
            }
            Expect(routeCourier != null && r.Board(routeCourier), "courier available for winning route");
            int next = routeCourier.Destination;
            r.Tick(2.4f + Math.Abs(next - r.Floor) * .8f, false);
            Expect(r.Arrive(next) == 1, "manual route reaches requested floor");
            Expect(r.Offboard(routeCourier) == OffboardResult.Happy, "manual route offboards correctly");
        }
        Expect(r.Happy >= 2, "happy deliveries remain reachable without a quota");
        Expect(!r.Won, "top-floor win remains separate from delivery quality");
        r.Arrive(ElevatorRound.Floors - 1);
        Expect(r.Won && r.PeakTime >= 0, "reaching the Dream Deck wins and records climb time");
        Debug.Log("CRAZY_ELEVATOR_CHECKS_PASS: six rider types, physical-fit boarding, boss hold, slow arrival, manual routing/offboarding, mood penalties, deadline and top-floor objective. Happy=" + r.Happy);
    }

    [MenuItem("Crazy Elevator/Build macOS Playable")]
    public static void BuildMac()
    {
        RunChecks();
        // Keep the custom intro as the first project-owned animation. Unity
        // Personal may still enforce its own splash, but this also applies the
        // setting for plans/build targets that permit disabling it.
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        Directory.CreateDirectory("Builds/macOS");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = "Builds/macOS/Crazy Elevator.app",
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("Crazy Elevator build failed: " + report.summary.result);
        Debug.Log("CRAZY_ELEVATOR_BUILD_PASS: " + Path.GetFullPath("Builds/macOS/Crazy Elevator.app"));
    }
}
