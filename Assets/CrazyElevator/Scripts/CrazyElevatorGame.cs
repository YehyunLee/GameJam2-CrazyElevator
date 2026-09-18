using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrazyElevator
{
    public sealed class CrazyElevatorGame : MonoBehaviour
    {
        enum Phase { Intro, Welcome, Tutorial, Boarding, Closing, Closed, Moving, Opening, Results }
        static readonly Color Ink = new Color32(43, 48, 78, 255);
        static readonly Color Teal = new Color32(75, 226, 202, 255);
        static readonly Color Cream = new Color32(255, 250, 234, 255);
        static readonly Color Coral = new Color32(255, 124, 104, 255);
        static readonly Color Sky = new Color32(112, 183, 255, 255);
        static readonly Color Gold = new Color32(255, 205, 82, 255);
        static readonly Color[] Palette =
        {
            new Color32(91, 168, 255, 255), Coral, Gold,
            new Color32(177, 120, 255, 255), new Color32(112, 204, 139, 255), new Color32(255, 154, 79, 255)
        };
        static readonly string[] FloorNames =
        {
            "LOBBY", "MAIL ROOM", "GARDENS", "OFFICES", "STUDIO", "CAFETERIA",
            "LIBRARY", "OBSERVATORY", "SKY LOUNGE", "ARCADE", "ROOFTOP", "DREAM DECK"
        };
        const float LowestFloorDoorTime = 8f;
        const float HighestFloorDoorTime = 3.2f;
        const float IntroBootGuardDuration = 5.5f;
        const float IntroRevealStart = 3.05f;
        const float IntroRevealDuration = 4.2f;
        ElevatorRound round;
        Phase phase;
        Camera eye;
        Transform stage, leftDoor, rightDoor, scenery;
        TextMesh floorSign, controlStatus;
        readonly Dictionary<Rider, Transform> figures = new Dictionary<Rider, Transform>();
        readonly Dictionary<Rider, TextMesh> bubbles = new Dictionary<Rider, TextMesh>();
        readonly Dictionary<Rider, TextMesh> destinationTags = new Dictionary<Rider, TextMesh>();
        readonly Dictionary<Rider, Vector3> cabinPositions = new Dictionary<Rider, Vector3>();
        readonly Dictionary<Rider, float> exiting = new Dictionary<Rider, float>();
        readonly Dictionary<Rider, Vector3> exitStarts = new Dictionary<Rider, Vector3>();
        readonly Dictionary<Collider, Rider> riderHits = new Dictionary<Collider, Rider>();
        readonly Dictionary<Collider, int> floorButtons = new Dictionary<Collider, int>();
        readonly Dictionary<int, Renderer> floorButtonVisuals = new Dictionary<int, Renderer>();
        readonly HashSet<Collider> openButtons = new HashSet<Collider>();
        readonly HashSet<Collider> closeButtons = new HashSet<Collider>();
        readonly List<TextMesh> controlStatuses = new List<TextMesh>();
        readonly Dictionary<Color, Material> materials = new Dictionary<Color, Material>();
        AudioSource speaker, music;
        AudioClip chime, ding, click, buzz, groove, stamp;
        GUIStyle title, large, body, small, buttonStyle, inkBody, inkLarge, inkSmall, logo, stampWord;
        float phaseTime, introTime, travelDuration, doors = 1, scale, offsetX, offsetY;
        float arrivalImpact;
        float bootTime;
        Vector3 cameraHome;
        Quaternion cameraHomeRotation;
        readonly Vector3 floorSignHome = new Vector3(0, 2.72f, -.18f);
        int destination, origin;
        int selectedFloor = -1;
        Collider openButton, closeButton;
        Rider draggedRider;
        Vector3 dragStart, dragOffset;
        Vector2 dragScreenStart;
        float dragPlaneLocalY;
        bool draggedWasBoarded;
        bool paused, tutorialSeen, musicMuted, stampPlayed;
        string notice = "Welcome aboard. Your shift starts when you are ready.";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<CrazyElevatorGame>() == null)
                new GameObject("Crazy Elevator • Game").AddComponent<CrazyElevatorGame>();
        }

        void Awake()
        {
            foreach (var camera in FindObjectsByType<Camera>()) camera.gameObject.SetActive(false);
            foreach (var light in FindObjectsByType<Light>()) light.enabled = false;
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            Time.timeScale = 1f;
            bootTime = Time.realtimeSinceStartup;
            round = new ElevatorRound();
            BuildStage();
            speaker = gameObject.AddComponent<AudioSource>(); speaker.volume = .24f;
            chime = Tone(660, .28f); ding = DingTone(); click = Tone(420, .08f); buzz = Tone(130, .18f); stamp = StampTone();
            music = gameObject.AddComponent<AudioSource>(); music.loop = true; music.volume = .16f;
            groove = Groove(); music.clip = groove; music.Play();
            phase = Phase.Intro;
            introTime = 0;
            doors = 0;
            SetDoors(doors);
            SyncFigures(0);
        }

        Material Mat(Color color)
        {
            if (materials.TryGetValue(color, out var existing)) return existing;
            // Runtime-generated meshes do not have a serialized renderer reference.
            // Use the Resources material so the player build keeps the URP shader;
            // Shader.Find alone can return null after player shader stripping.
            var template = Resources.Load<Material>("CrazyElevatorLit");
            var mat = template != null ? new Material(template) : new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            mat.color = color;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", .32f);
            materials.Add(color, mat); return mat;
        }

        Transform Shape(string name, PrimitiveType kind, Vector3 position, Vector3 size, Color color, Transform parent)
        {
            var g = GameObject.CreatePrimitive(kind); g.name = name;
            g.transform.SetParent(parent, false); g.transform.localPosition = position; g.transform.localScale = size;
            g.GetComponent<Renderer>().sharedMaterial = Mat(color);
            return g.transform;
        }
        Transform Box(string name, Vector3 pos, Vector3 size, Color color, Transform parent = null)
            => Shape(name, PrimitiveType.Cube, pos, size, color, parent == null ? stage : parent);

        TextMesh Sign(string text, Vector3 position, float size, Color color)
        {
            var g = new GameObject("Sign • " + text); g.transform.SetParent(stage, false); g.transform.localPosition = position;
            var t = g.AddComponent<TextMesh>(); t.text = text; t.fontSize = 64; t.characterSize = size;
            t.anchor = TextAnchor.MiddleCenter; t.alignment = TextAlignment.Center; t.color = color;
            // TextMesh fronts face negative Z, toward the observation camera.
            return t;
        }

        void BuildStage()
        {
            stage = new GameObject("Elevator cutaway").transform;
            var backdrop = new GameObject("Full screen clear").AddComponent<Camera>();
            backdrop.depth = -10; backdrop.cullingMask = 0; backdrop.clearFlags = CameraClearFlags.SolidColor; backdrop.backgroundColor = Ink;
            var c = new GameObject("Security camera"); eye = c.AddComponent<Camera>(); c.AddComponent<AudioListener>();
            // Straight-on security-camera view from the back wall. The door stays
            // centred like a real elevator, with the mirrored control columns at
            // the left and right edges of the frame.
            eye.transform.position = new Vector3(0f, 2.08f, 4.86f);
            eye.transform.LookAt(new Vector3(0, 1.42f, -.05f)); eye.orthographic = false; eye.fieldOfView = 62;
            cameraHome = eye.transform.position; cameraHomeRotation = eye.transform.rotation;
            eye.clearFlags = CameraClearFlags.SolidColor; eye.backgroundColor = new Color32(42, 48, 66, 255);
            eye.nearClipPlane = .1f; eye.farClipPlane = 100;
            RenderSettings.ambientLight = new Color(.63f, .68f, .76f);
            var l = new GameObject("Warm ceiling light").AddComponent<Light>(); l.type = LightType.Directional;
            l.transform.rotation = Quaternion.Euler(48, -35, 0); l.intensity = 1.6f; l.shadows = LightShadows.Soft;
            var fill = new GameObject("Cool fill").AddComponent<Light>(); fill.type = LightType.Directional;
            fill.transform.rotation = Quaternion.Euler(28, 130, 0); fill.intensity = .65f; fill.color = new Color(.6f, .85f, 1);

            Box("Floating foundation", new Vector3(0, -.38f, 0), new Vector3(7.5f, .65f, 8), Ink);
            Box("Cabin floor", new Vector3(0, -.03f, 2.58f), new Vector3(5.8f, .16f, 5.08f), Cream);
            Box("Hall floor", new Vector3(0, -.02f, -2), new Vector3(7, .14f, 3.9f), new Color32(173, 154, 232, 255));
            Box("Runner", new Vector3(0, .065f, -1.85f), new Vector3(2.1f, .015f, 3.2f), Coral);
            for (int x = -3; x <= 3; x++) Box("Hall tile", new Vector3(x, .066f, -2), new Vector3(.025f, .01f, 3.7f), Ink);
            for (int z = -3; z <= 3; z++) Box("Floor seam", new Vector3(0, .069f, z), new Vector3(5.7f, .008f, .018f), new Color(.55f, .56f, .55f));
            Box("Hall back wall", new Vector3(0, 1.55f, -3.95f), new Vector3(7.2f, 3.2f, .18f), new Color32(218, 222, 216, 255));
            Box("Hall upper band", new Vector3(0, 2.78f, -3.82f), new Vector3(7f, .46f, .08f), new Color32(88, 105, 133, 255));
            for (int i = -3; i <= 3; i++) Box("Hall wall seam", new Vector3(i, 1.55f, -3.82f), new Vector3(.025f, 2.8f, .05f), new Color32(174, 183, 184, 255));
            Box("Hall ceiling", new Vector3(0, 3.16f, -2.0f), new Vector3(7.2f, .14f, 4.0f), new Color32(238, 236, 226, 255));
            Box("Back wall", new Vector3(0, 1.5f, 5.18f), new Vector3(5.9f, 3, .18f), new Color32(86, 101, 122, 255));
            Box("Left wall", new Vector3(-2.9f, 1.5f, 2.58f), new Vector3(.18f, 3, 5.0f), new Color32(164, 117, 83, 255));
            Box("Right wall", new Vector3(2.9f, 1.5f, 2.58f), new Vector3(.18f, 3, 5.0f), new Color32(164, 117, 83, 255));
            for (int i = -2; i <= 2; i++) Box("Back metal rib", new Vector3(i, 1.5f, 5.06f), new Vector3(.035f, 2.8f, .05f), new Color32(171, 183, 198, 255));
            Box("Left handrail", new Vector3(-2.67f, .92f, 2.58f), new Vector3(.08f, .08f, 4.18f), Cream);
            Box("Right handrail", new Vector3(2.67f, .92f, 2.58f), new Vector3(.08f, .08f, 4.18f), Cream);
            Box("Door jamb left", new Vector3(-2.8f, 1.55f, 0), new Vector3(.25f, 3.1f, .28f), Cream);
            Box("Door jamb right", new Vector3(2.8f, 1.55f, 0), new Vector3(.25f, 3.1f, .28f), Cream);
            Box("Door header", new Vector3(0, 3.05f, 0), new Vector3(5.8f, .2f, .28f), Cream);
            Box("Threshold", new Vector3(0, .10f, 0), new Vector3(5.6f, .06f, .32f), new Color32(244, 192, 71, 255));
            for (int i = 0; i < 14; i++) Box("Threshold stripe", new Vector3(-2.55f + i * .39f, .134f, 0), new Vector3(.16f, .01f, .3f), Ink);
            leftDoor = Box("Sliding door L", new Vector3(-1.32f, 1.35f, .08f), new Vector3(2.64f, 2.65f, .1f), new Color32(255, 242, 198, 255));
            rightDoor = Box("Sliding door R", new Vector3(1.32f, 1.35f, .08f), new Vector3(2.64f, 2.65f, .1f), new Color32(255, 242, 198, 255));
            // A compact in-world floor display replaces the old top HUD.
            floorSign = Sign("00", floorSignHome, .04f, Cream);
            // TextMesh is created facing the opposite side of the cabin in the
            // player build; turn the display toward the straight-on camera.
            floorSign.transform.rotation = Quaternion.Euler(0, 180, 0);
            BuildControlPanel();
            // Moving stripes outside the cutaway give a sense of vertical travel.
            scenery = new GameObject("Passing shaft lights").transform; scenery.SetParent(stage);
            for (int i = 0; i < 12; i++) Box("Shaft lamp", new Vector3(-3.55f, i * 1.5f - 6, 2.2f), new Vector3(.12f, .65f, .2f), Teal, scenery);
            Box("Hall bench", new Vector3(-2.75f, .4f, -2.1f), new Vector3(.6f, .18f, 2.1f), Cream);
            Box("Plant pot", new Vector3(3, .28f, -3), new Vector3(.6f, .5f, .6f), Coral);
            Shape("Plant", PrimitiveType.Sphere, new Vector3(3, 1, -3), new Vector3(.75f, 1.25f, .75f), Teal, stage);
        }

        void BuildControlPanel()
        {
            // Real elevator cars place controls on the front side walls beside
            // the doors. The plates are mounted close to the walls rather than
            // floating in the cabin, and each has a two-column button layout.
            BuildControlColumn(-2.42f, 0, ElevatorRound.Floors / 2);
            BuildControlColumn(2.42f, ElevatorRound.Floors / 2, ElevatorRound.Floors / 2);
        }

        void BuildControlColumn(float panelX, int firstFloor, int floorCount)
        {
            const float panelZ = .72f;
            const float faceZ = panelZ + .11f;
            Transform trim = Box("Wall control trim", new Vector3(panelX, 1.30f, panelZ), new Vector3(.78f, 2.28f, .16f), new Color32(171, 183, 198, 255));
            Transform panel = Box("Wall control panel", new Vector3(panelX, 1.30f, panelZ + .08f), new Vector3(.64f, 2.12f, .08f), Ink);
            string range = firstFloor.ToString("00") + "–" + (firstFloor + floorCount - 1).ToString("00");
            TextMesh heading = Sign(range, new Vector3(panelX, 2.20f, faceZ), .012f, Cream);
            heading.transform.rotation = Quaternion.Euler(0, 180, 0);
            TextMesh status = Sign("READY", new Vector3(panelX, .23f, faceZ), .009f, Cream);
            status.transform.rotation = Quaternion.Euler(0, 180, 0);
            controlStatuses.Add(status);
            if (controlStatus == null || panelX < 0) controlStatus = status;

            Collider open = ControlButton("OPEN", new Vector3(panelX, 1.89f, faceZ), Teal, 0, 0);
            Collider close = ControlButton("CLOSE", new Vector3(panelX, 1.67f, faceZ), Coral, 0, 0);
            openButtons.Add(open); closeButtons.Add(close);
            for (int i = 0; i < floorCount; i++)
            {
                int row = i / 2, column = i % 2;
                float y = 1.35f - row * .28f;
                float x = panelX + (column == 0 ? -.14f : .14f);
                int floor = firstFloor + i;
                Collider button = ControlButton(floor.ToString("00"), new Vector3(x, y, faceZ), Teal, floor + 1, 0, true);
                floorButtons[button] = floor;
                Renderer visual = button.GetComponent<Renderer>();
                visual.material = new Material(visual.sharedMaterial);
                floorButtonVisuals[floor] = visual;
            }
            RefreshFloorButtons();
        }

        Collider ControlButton(string label, Vector3 position, Color color, int action, float yaw, bool roundButton = false)
        {
            Transform button = roundButton
                ? Shape("Round button " + label, PrimitiveType.Cylinder, position, new Vector3(.085f, .035f, .085f), color, stage)
                : Box("Button " + label, position, new Vector3(.32f, .10f, .06f), color);
            button.localRotation = roundButton ? Quaternion.Euler(90, yaw, 0) : Quaternion.Euler(0, yaw, 0);
            Collider collider = button.GetComponent<Collider>();
            if (action > 0) floorButtons[collider] = action - 1;
            Vector3 normal = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            TextMesh text = Sign(label, position + normal * (roundButton ? .055f : .045f), roundButton ? .008f : .010f, Ink);
            text.transform.rotation = Quaternion.Euler(0, yaw - 180, 0);
            return collider;
        }

        Transform MakeRider(Rider p)
        {
            var root = new GameObject(p.Name + " • " + p.Kind).transform; root.SetParent(stage, false);
            var color = Palette[p.Color % Palette.Length];
            Shape("Jacket", PrimitiveType.Capsule, new Vector3(0, .76f, 0), new Vector3(.48f, .45f, .38f), color, root);
            Shape("Head", PrimitiveType.Sphere, new Vector3(0, 1.4f, 0), Vector3.one * .39f, new Color32(233, 191, 153, 255), root);
            Shape("Hair", PrimitiveType.Sphere, new Vector3(0, 1.53f, .025f), new Vector3(.40f, .19f, .4f), Ink, root);
            for (int s = -1; s <= 1; s += 2)
            {
                Box("Leg", new Vector3(s * .13f, .24f, 0), new Vector3(.15f, .42f, .18f), Ink, root);
                Box("Shoe", new Vector3(s * .13f, .08f, -.08f), new Vector3(.19f, .13f, .3f), Cream, root);
                Shape("Eye", PrimitiveType.Sphere, new Vector3(s * .075f, 1.43f, -.175f), Vector3.one * .05f, Ink, root);
                Box("Arm", new Vector3(s * .30f, .82f, 0), new Vector3(.14f, .5f, .18f), color, root);
            }
            if (p.Kind == "COURIER")
                Box("Parcel", new Vector3(.42f, .45f, -.35f), new Vector3(.55f, .7f, .5f), new Color32(183, 139, 87, 255), root);
            else if (p.Kind == "PREGNANT")
                Shape("Baby bump", PrimitiveType.Sphere, new Vector3(0, .76f, -.26f), new Vector3(.48f, .40f, .34f), color, root);
            else if (p.Kind == "INTERVIEW")
                Box("Tie", new Vector3(0, .94f, -.20f), new Vector3(.08f, .34f, .035f), Ink, root);
            else if (p.Kind == "BOSS")
            {
                Box("Boss badge", new Vector3(-.18f, 1.00f, -.20f), new Vector3(.15f, .10f, .035f), Gold, root);
                Box("Boss hat", new Vector3(0, 1.70f, 0), new Vector3(.46f, .08f, .42f), Ink, root);
            }
            else if (p.Kind == "ELDERLY")
                Shape("Cane", PrimitiveType.Cylinder, new Vector3(.38f, .43f, -.12f), new Vector3(.035f, .40f, .035f), new Color32(130, 87, 57, 255), root);
            else if (p.Kind == "GROUP")
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    Shape("Friend jacket", PrimitiveType.Capsule, new Vector3(side * .40f, .70f, .18f), new Vector3(.30f, .36f, .28f), color, root);
                    Shape("Friend head", PrimitiveType.Sphere, new Vector3(side * .40f, 1.22f, .18f), Vector3.one * .27f, new Color32(233, 191, 153, 255), root);
                }
            }
            var speechObject = new GameObject("Speech bubble");
            speechObject.transform.SetParent(root, false); speechObject.transform.localPosition = new Vector3(0, 1.82f, 0);
            var speech = speechObject.AddComponent<TextMesh>(); speech.fontSize = 36; speech.characterSize = .020f;
            speech.anchor = TextAnchor.MiddleCenter; speech.alignment = TextAlignment.Center;
            bubbles[p] = speech;

            var destinationObject = new GameObject("Destination badge");
            destinationObject.transform.SetParent(root, false); destinationObject.transform.localPosition = new Vector3(0, 2.08f, 0);
            var destinationTag = destinationObject.AddComponent<TextMesh>();
            destinationTag.text = p.Badge + "  " + p.Destination.ToString("00");
            destinationTag.fontSize = 48; destinationTag.characterSize = .024f; destinationTag.fontStyle = FontStyle.Bold;
            destinationTag.anchor = TextAnchor.MiddleCenter; destinationTag.alignment = TextAlignment.Center;
            destinationTags[p] = destinationTag;
            // The badge is a second, generous click target. It stays visible
            // above the crowd, so a rider remains selectable even when another
            // model partly blocks their body from the straight-on camera.
            var badgeTarget = destinationObject.AddComponent<BoxCollider>();
            badgeTarget.size = new Vector3(p.Kind == "GROUP" ? 1.35f : 1.0f, .34f, .22f);
            var clickTarget = root.gameObject.AddComponent<CapsuleCollider>();
            clickTarget.center = new Vector3(0, .85f, 0); clickTarget.radius = p.Kind == "GROUP" ? .72f : .42f; clickTarget.height = 1.75f;
            foreach (var collider in root.GetComponentsInChildren<Collider>()) riderHits[collider] = p;
            return root;
        }

        Vector3 RiderPosition(Rider p)
        {
            int slot = 0;
            foreach (var other in round.Riders)
            {
                if (other == p) break;
                if (p.Boarded ? other.Boarded && !other.Resolved : !other.Boarded && !other.Resolved && other.Origin == round.Floor) slot++;
            }
            if (p.Boarded)
            {
                if (cabinPositions.TryGetValue(p, out Vector3 placed)) return placed;
                // Defensive fallback for restored/runtime-created riders. Normal
                // boarding keeps the exact continuous position chosen by the player.
                float[] cabinX = { -1.65f, -.55f, .55f, 1.65f };
                return new Vector3(cabinX[Mathf.Min(slot, cabinX.Length - 1)], .12f, 1.08f);
            }
            return new Vector3(-2f + slot * 2f, .12f, -1.45f - Mathf.Min(1.2f, p.Arrival * .20f));
        }

        void SyncFigures(float dt)
        {
            var completedExits = new List<Rider>();
            foreach (var p in round.Riders)
            {
                bool isExiting = exiting.TryGetValue(p, out float exitTime);
                // TextMesh labels do not use the same occlusion as the solid
                // doors, so explicitly hide the hall queue unless the doorway
                // is open enough to interact with it.
                bool hallVisible = phase == Phase.Boarding || phase == Phase.Opening && doors > .72f;
                bool visible = isExiting || !p.Resolved && (p.Boarded || p.Origin == round.Floor && hallVisible && round.IsOffered(p));
                if (!figures.TryGetValue(p, out var figure))
                {
                    if (!visible) continue;
                    figure = MakeRider(p); figure.localPosition = RiderPosition(p); figures.Add(p, figure);
                }
                figure.gameObject.SetActive(visible);
                if (!visible) continue;

                if (isExiting)
                {
                    if ((phase == Phase.Opening && doors > .55f) || phase == Phase.Boarding) exitTime += dt / 1.15f;
                    exiting[p] = exitTime;
                    if (exitTime >= 0)
                    {
                        float progress = EaseOut(Mathf.Clamp01(exitTime));
                        Vector3 start = exitStarts[p];
                        Vector3 target = new Vector3(-1.35f + (p.Color % 3) * 1.35f, .12f, -2.1f);
                        figure.localPosition = Vector3.Lerp(start, target, progress);
                        figure.localRotation = Quaternion.Euler(0, 180, 0);
                        if (exitTime >= 1f) completedExits.Add(p);
                    }
                }
                else
                {
                    if (p != draggedRider)
                    {
                        var target = RiderPosition(p);
                        figure.localPosition = Vector3.MoveTowards(figure.localPosition, target, dt * 5);
                        figure.localRotation = Quaternion.Euler(0, p.Boarded ? 0 : 180, 0);
                    }
                }
                if (bubbles.TryGetValue(p, out var bubble))
                {
                    bubble.text = BubbleText(p);
                    bubble.color = IsComplaining(p) ? Coral : Cream;
                    bubble.transform.rotation = Quaternion.LookRotation(bubble.transform.position - eye.transform.position);
                }
                if (destinationTags.TryGetValue(p, out var destinationTag))
                {
                    destinationTag.text = p.Badge + "  " + p.Destination.ToString("00");
                    destinationTag.color = p.Mood < 2 ? Coral : p.Boarded || isExiting ? Teal : Gold;
                    destinationTag.transform.rotation = Quaternion.LookRotation(destinationTag.transform.position - eye.transform.position);
                }
            }
            foreach (var p in completedExits)
            {
                exiting.Remove(p); exitStarts.Remove(p);
                if (figures.TryGetValue(p, out var figure)) figure.gameObject.SetActive(false);
            }
        }

        string BubbleText(Rider p)
        {
            if (exiting.ContainsKey(p)) return "\"" + (string.IsNullOrEmpty(p.Status) ? "Made it!" : p.Status) + "\"";
            if (p.Boarded && p.Destination == round.Floor && phase == Phase.Boarding) return "\"My stop! Drag me out.\"";
            if (p.Boarded && !string.IsNullOrEmpty(p.Status)) return "\"" + p.Status + "\"";
            if (p.Boarded && p.HoldRequired > 0 && !p.HoldSatisfied) return "\"Hold OPEN!\"";
            if (p.Boarded) return IsComplaining(p) ? "\"Please hurry!\"" : "\"Floor " + p.Destination.ToString("00") + "\"";
            return "\"" + p.Request + "\"";
        }

        bool IsComplaining(Rider p)
        {
            return p.Mood < 2 || p.Remaining < p.Patience * .35f;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (phase == Phase.Intro)
            {
                // Unity Personal can keep its own startup splash visible while
                // the first scene is loading. Hold our intro clock until that
                // window has passed so the custom animation cannot finish behind
                // the engine splash.
                if (Time.realtimeSinceStartup - bootTime < IntroBootGuardDuration) return;
                // Use wall-clock time here rather than accumulating frame
                // deltas. A player window can resume with one unusually large
                // delta after the engine splash or focus changes; absolute time
                // keeps the logo and centre reveal deterministic.
                introTime = Mathf.Max(0f, Time.realtimeSinceStartup - bootTime - IntroBootGuardDuration);
                if (!stampPlayed && introTime >= 1.12f) { stampPlayed = true; Play(stamp); }
                float opening = Mathf.Clamp01((introTime - IntroRevealStart) / IntroRevealDuration);
                SetDoors(EaseOut(opening));
                if (introTime >= IntroRevealStart + IntroRevealDuration + .15f) { phase = Phase.Welcome; introTime = 0; SetDoors(1); }
                return;
            }
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame && phase != Phase.Welcome && phase != Phase.Results) paused = !paused;
            if (keyboard != null && keyboard.mKey.wasPressedThisFrame) { musicMuted = !musicMuted; if (music != null) music.mute = musicMuted; }
            if (keyboard != null && keyboard.enterKey.wasPressedThisFrame && (phase == Phase.Welcome || phase == Phase.Tutorial || phase == Phase.Results)) StartOrContinue();
            if (paused || phase == Phase.Welcome || phase == Phase.Tutorial || phase == Phase.Results) return;
            HandleWorldInput();
            float dt = Time.deltaTime;
            round.Tick(dt, phase == Phase.Boarding);
            if (round.Finished && exiting.Count == 0) { phase = Phase.Results; Play(chime); ResetCameraMotion(); return; }
            phaseTime += dt;
            switch (phase)
            {
                case Phase.Closing:
                    doors = 1 - Mathf.Clamp01(phaseTime / .65f);
                    if (phaseTime >= .65f)
                    {
                        doors = 0;
                        if (destination >= 0 && destination != round.Floor) StartMoving();
                        else
                        {
                            phase = Phase.Closed; phaseTime = 0;
                            SetControlStatus("PICK A FLOOR");
                            notice = "Doors closed. Choose a floor button.";
                        }
                    }
                    break;
                case Phase.Closed:
                    doors = 0;
                    break;
                case Phase.Moving:
                    scenery.localPosition = new Vector3(0, (phaseTime * 4 * Mathf.Sign(origin - destination)) % 1.5f, 0);
                    AnimateFloorIndicator();
                    if (phaseTime >= travelDuration)
                    {
                        int count = round.Arrive(destination);
                        floorSign.text = destination.ToString("00");
                        floorSign.transform.localPosition = floorSignHome; floorSign.characterSize = .04f;
                        selectedFloor = -1; destination = -1; RefreshFloorButtons(); SetControlStatus("READY");
                        phase = Phase.Opening; phaseTime = 0; arrivalImpact = 1f; Play(ding);
                        notice = count > 0 ? "This is " + count + " rider's stop. Drag them through the doorway." : "Doors opening. Drag waiting passengers into clear cabin spaces.";
                    }
                    break;
                case Phase.Opening:
                    doors = Mathf.Clamp01(phaseTime / .65f);
                    if (phaseTime >= .65f) { phase = Phase.Boarding; phaseTime = 0; }
                    break;
                case Phase.Boarding:
                    // A real elevator does not remain stranded with the doors
                    // shut when nobody presses a button. Letting the timer run
                    // out closes the doors and sends the car to a sensible stop;
                    // the explicit CLOSE button remains a deliberate manual
                    // close-and-wait action.
                    if (phaseTime >= DoorHoldDurationAtCurrentFloor()) AutoDepartAfterTimeout();
                    break;
            }
            SetDoors(doors);
            SyncFigures(dt);
            UpdateRideMotion(dt);
        }

        float EaseOut(float value)
        {
            value = Mathf.Clamp01(value);
            return 1 - Mathf.Pow(1 - value, 3);
        }

        void SetDoors(float openness)
        {
            doors = Mathf.Clamp01(openness);
            if (leftDoor == null || rightDoor == null) return;
            leftDoor.localPosition = new Vector3(-1.32f - doors * 2.7f, 1.35f, .08f);
            rightDoor.localPosition = new Vector3(1.32f + doors * 2.7f, 1.35f, .08f);
            leftDoor.gameObject.SetActive(doors < .98f); rightDoor.gameObject.SetActive(doors < .98f);
        }

        void AnimateFloorIndicator()
        {
            int distance = Mathf.Abs(destination - origin);
            if (distance == 0) return;
            float progress = Mathf.Clamp01(phaseTime / Mathf.Max(.01f, travelDuration));
            float exactStep = progress * distance;
            int completedStep = Mathf.Min(distance, Mathf.FloorToInt(exactStep + .001f));
            int direction = destination > origin ? 1 : -1;
            int shownFloor = origin + direction * completedStep;
            float betweenFloors = exactStep - completedStep;
            float pulse = Mathf.Sin(Mathf.Clamp01(betweenFloors) * Mathf.PI);
            floorSign.text = shownFloor.ToString("00");
            floorSign.transform.localPosition = floorSignHome + Vector3.up * (.055f * pulse);
            floorSign.characterSize = .04f + .006f * pulse;
        }

        void UpdateRideMotion(float dt)
        {
            if (eye == null) return;
            if (phase == Phase.Moving)
            {
                float progress = Mathf.Clamp01(phaseTime / Mathf.Max(.01f, travelDuration));
                float ramp = Mathf.Clamp01(Mathf.Min(progress / .12f, (1 - progress) / .12f));
                float strength = (destination > origin ? .024f : .016f) * ramp;
                float clock = Time.unscaledTime;
                Vector3 localShake = new Vector3(Mathf.Sin(clock * 51f), Mathf.Sin(clock * 67f) * .75f, 0) * strength;
                eye.transform.position = cameraHome + cameraHomeRotation * localShake;
                eye.transform.rotation = cameraHomeRotation * Quaternion.Euler(Mathf.Sin(clock * 39f) * strength * 18f, 0, Mathf.Sin(clock * 43f) * strength * 12f);
                return;
            }
            if (arrivalImpact > 0)
            {
                arrivalImpact = Mathf.Max(0, arrivalImpact - dt * 2.8f);
                float kick = Mathf.Sin((1 - arrivalImpact) * Mathf.PI * 4f) * arrivalImpact;
                eye.transform.position = cameraHome + cameraHomeRotation * new Vector3(0, kick * .075f, 0);
                eye.transform.rotation = cameraHomeRotation * Quaternion.Euler(kick * 1.1f, 0, 0);
                return;
            }
            ResetCameraMotion();
        }

        void ResetCameraMotion()
        {
            if (eye == null) return;
            eye.transform.position = cameraHome;
            eye.transform.rotation = cameraHomeRotation;
        }

        void SetControlStatus(string text)
        {
            foreach (var status in controlStatuses) if (status != null) status.text = text;
        }

        void RefreshFloorButtons()
        {
            foreach (var pair in floorButtonVisuals)
            {
                Color color = pair.Key == selectedFloor ? Gold : pair.Key == round.Floor ? Cream : Teal;
                if (pair.Value != null && pair.Value.material != null) pair.Value.material.color = color;
            }
        }

        void CloseWithoutDestination()
        {
            if (phase != Phase.Boarding) return;
            selectedFloor = -1; destination = -1; RefreshFloorButtons();
            phase = Phase.Closing; phaseTime = 0; SetControlStatus("CLOSING"); Play(click);
        }

        int AutomaticNextFloor()
        {
            var riderStops = new List<int>();
            foreach (var rider in round.Riders)
            {
                if (!rider.Boarded || rider.Resolved || rider.Destination == round.Floor) continue;
                if (!riderStops.Contains(rider.Destination)) riderStops.Add(rider.Destination);
            }
            if (riderStops.Count > 0)
                return riderStops[Random.Range(0, riderStops.Count)];

            // With an empty car, stay local rather than jumping across the
            // whole building. The small random window makes an unattended
            // elevator feel alive while keeping the next queue reachable.
            var nearby = new List<int>();
            for (int distance = 1; distance <= 3; distance++)
            {
                int above = round.Floor + distance;
                int below = round.Floor - distance;
                if (above < ElevatorRound.Floors) nearby.Add(above);
                if (below >= 0) nearby.Add(below);
            }
            return nearby.Count > 0 ? nearby[Random.Range(0, nearby.Count)] : (round.Floor + 1) % ElevatorRound.Floors;
        }

        void AutoDepartAfterTimeout()
        {
            if (phase != Phase.Boarding) return;
            int next = AutomaticNextFloor();
            selectedFloor = next; destination = next; RefreshFloorButtons();
            phase = Phase.Closing; phaseTime = 0;
            SetControlStatus("AUTO " + next.ToString("00")); Play(click);
            notice = "Doors timed out. Continuing to " + FloorNames[next] + ".";
        }

        float DoorHoldDurationAtCurrentFloor()
        {
            if (round == null || ElevatorRound.Floors <= 1) return LowestFloorDoorTime;
            float floorProgress = Mathf.Clamp01(round.Floor / (float)(ElevatorRound.Floors - 1));
            return Mathf.Lerp(LowestFloorDoorTime, HighestFloorDoorTime, floorProgress);
        }

        void StartMoving()
        {
            if (destination < 0 || destination == round.Floor) return;
            origin = round.Floor;
            int missed = round.LeaveFloor();
            travelDuration = 1.1f + Mathf.Abs(destination - origin) * .8f;
            phase = Phase.Moving; phaseTime = 0;
            SetControlStatus("GOING " + destination.ToString("00"));
            notice = missed > 0 ? "You passed someone's floor. Their mood dropped." : "Next stop: " + FloorNames[destination] + ".";
        }

        void DepartToFloor(int floor)
        {
            if (paused || floor < 0 || floor >= ElevatorRound.Floors) return;
            if (floor == round.Floor)
            {
                selectedFloor = -1; destination = -1; RefreshFloorButtons(); SetControlStatus("CURRENT FLOOR");
                return;
            }
            if (phase != Phase.Boarding && phase != Phase.Closing && phase != Phase.Closed) return;
            selectedFloor = floor; destination = floor; RefreshFloorButtons();
            SetControlStatus("GOING " + destination.ToString("00")); Play(click);
            if (phase == Phase.Closed) StartMoving();
            else if (phase == Phase.Boarding) { phase = Phase.Closing; phaseTime = 0; }
        }

        float PartyRadius(Rider rider)
            => rider.Space >= 3 ? .72f : rider.Space == 2 ? .52f : .34f;

        bool IsInsideCabin(Rider rider, Vector3 position)
        {
            float radius = PartyRadius(rider);
            return Mathf.Abs(position.x) <= 2.18f - radius * .45f
                && position.z >= .42f + radius * .20f
                && position.z <= 3.48f - radius * .25f;
        }

        bool CabinPlacementClear(Rider rider, Vector3 position)
        {
            foreach (var other in round.Riders)
            {
                if (other == rider || !other.Boarded || other.Resolved) continue;
                Vector3 otherPosition = cabinPositions.TryGetValue(other, out Vector3 placed)
                    ? placed : figures.TryGetValue(other, out Transform figure) ? figure.localPosition : RiderPosition(other);
                Vector2 delta = new Vector2(position.x - otherPosition.x, position.z - otherPosition.z);
                if (delta.magnitude < PartyRadius(rider) + PartyRadius(other) + .08f) return false;
            }
            return true;
        }

        bool CursorIsBeyondDoor(Vector2 cursor)
        {
            Vector3 threshold = eye.WorldToScreenPoint(stage.TransformPoint(new Vector3(0, .12f, .15f)));
            return cursor.y > threshold.y + 18f;
        }

        void BeginRiderDrag(Rider rider, Vector2 cursor)
        {
            if (rider == null || rider.Resolved || !figures.TryGetValue(rider, out Transform figure)) return;
            if (!rider.Boarded && rider.Arrival > 0)
            {
                SetControlStatus("STILL COMING");
                notice = "That passenger has not reached the door yet.";
                return;
            }
            draggedRider = rider;
            draggedWasBoarded = rider.Boarded;
            dragStart = figure.localPosition;
            dragScreenStart = cursor;
            Vector3 startWorld = stage.TransformPoint(dragStart);
            Vector3 grabbedWorld = startWorld;
            float nearestHit = float.MaxValue;
            Ray ray = eye.ScreenPointToRay(cursor);
            foreach (RaycastHit hit in Physics.RaycastAll(ray, 100f))
            {
                if (!riderHits.TryGetValue(hit.collider, out Rider hitRider) || hitRider != rider || hit.distance >= nearestHit) continue;
                nearestHit = hit.distance;
                grabbedWorld = hit.point;
            }
            if (nearestHit == float.MaxValue)
            {
                // Screen-space badge selection is deliberately generous. If
                // the cursor is just outside its collider, construct the same
                // grab point on a plane through the visible badge so starting
                // a drag never makes the rider jump.
                float handleLocalY = destinationTags.TryGetValue(rider, out TextMesh badge)
                    ? stage.InverseTransformPoint(badge.transform.position).y : dragStart.y + 1f;
                Plane handlePlane = new Plane(stage.up, stage.TransformPoint(new Vector3(0, handleLocalY, 0)));
                if (handlePlane.Raycast(ray, out float handleEnter)) grabbedWorld = ray.GetPoint(handleEnter);
            }
            dragPlaneLocalY = stage.InverseTransformPoint(grabbedWorld).y;
            dragOffset = startWorld - grabbedWorld;
            SetControlStatus(rider.Boarded ? "REARRANGE / EXIT" : "DRAG INSIDE");
        }

        void UpdateRiderDrag(Vector2 cursor)
        {
            if (draggedRider == null || !figures.TryGetValue(draggedRider, out Transform figure)) return;
            // Intersect the pointer ray with a plane at the exact height where
            // the rider was grabbed. The grabbed point therefore remains under
            // the cursor, while the stored offset keeps the model grounded.
            Plane dragPlane = new Plane(stage.up, stage.TransformPoint(new Vector3(0, dragPlaneLocalY, 0)));
            Ray ray = eye.ScreenPointToRay(cursor);
            if (!dragPlane.Raycast(ray, out float enter)) return;
            Vector3 world = ray.GetPoint(enter) + dragOffset;
            Vector3 local = stage.InverseTransformPoint(world);
            local.x = Mathf.Clamp(local.x, -2.55f, 2.55f);
            local.z = Mathf.Clamp(local.z, -2.65f, 3.45f);
            local.y = .12f;
            figure.localPosition = local;
            bool exitingCabin = phase == Phase.Boarding && draggedWasBoarded && CursorIsBeyondDoor(cursor);
            SetControlStatus(exitingCabin ? "RELEASE TO EXIT" : IsInsideCabin(draggedRider, local)
                ? CabinPlacementClear(draggedRider, local) ? "RELEASE TO PLACE" : "SPACE OCCUPIED"
                : "DRAG INTO CABIN");
        }

        void FinishRiderDrag(Vector2 cursor)
        {
            Rider rider = draggedRider;
            if (rider == null || !figures.TryGetValue(rider, out Transform figure)) { draggedRider = null; return; }
            bool moved = (cursor - dragScreenStart).sqrMagnitude >= 100f;
            draggedRider = null;

            if (!moved)
            {
                figure.localPosition = dragStart;
                SetControlStatus("DRAG THE RIDER");
                notice = "Drag a waiting rider into the cabin, or drag an onboard rider to a new spot.";
                return;
            }

            if (phase == Phase.Boarding && draggedWasBoarded && CursorIsBeyondDoor(cursor))
            {
                cabinPositions.Remove(rider);
                QueueRiderExit(rider, figure.localPosition);
                return;
            }

            Vector3 released = figure.localPosition;
            if (IsInsideCabin(rider, released))
            {
                if (!CabinPlacementClear(rider, released))
                {
                    figure.localPosition = dragStart;
                    SetControlStatus("MAKE MORE SPACE"); Play(buzz);
                    notice = "That floor space is occupied. Move riders aside or toward the back and try again.";
                    return;
                }
                if (draggedWasBoarded || round.Board(rider))
                {
                    cabinPositions[rider] = released;
                    SetControlStatus(draggedWasBoarded ? "REARRANGED" : "ON BOARD");
                    notice = draggedWasBoarded ? "Cabin rearranged." : "Passenger boarded. Choose another rider or select a floor.";
                    Play(click); return;
                }
                figure.localPosition = dragStart;
                SetControlStatus("CANNOT BOARD"); Play(buzz);
                notice = "That passenger cannot board right now.";
                return;
            }

            figure.localPosition = dragStart;
            SetControlStatus("DROP INSIDE");
            notice = draggedWasBoarded ? "Keep the rider inside, or drag them through the doorway to exit." : "Drag the passenger across the threshold into a clear cabin space.";
        }

        void QueueRiderExit(Rider rider, Vector3 start)
        {
            if (rider == null || !rider.Boarded || !figures.TryGetValue(rider, out var figure)) return;
            OffboardResult result = round.Offboard(rider);
            if (result == OffboardResult.None) return;
            exitStarts[rider] = start;
            exiting[rider] = -exiting.Count * .18f;
            phaseTime = 0;
            if (result == OffboardResult.WrongFloor) { SetControlStatus("WRONG FLOOR"); Play(buzz); }
            else { SetControlStatus(result == OffboardResult.Happy ? "HAPPY EXIT" : "LATE EXIT"); Play(click); }
        }

        bool TryPickRider(Vector2 cursor, Ray ray, bool waitingOnly, out Rider selected)
        {
            selected = null;

            // Destination badges are the clearest drag handles in the
            // straight-on view. Select the badge nearest the cursor in screen
            // space without letting a nearer 3D body block it.
            float badgeRadius = Mathf.Clamp(Screen.height * .055f, 38f, 64f);
            float bestBadgeDistance = badgeRadius * badgeRadius;
            foreach (var pair in destinationTags)
            {
                Rider rider = pair.Key;
                TextMesh badge = pair.Value;
                if (rider.Resolved || waitingOnly && rider.Boarded || badge == null || !badge.gameObject.activeInHierarchy) continue;
                Vector3 screen = eye.WorldToScreenPoint(badge.transform.position);
                if (screen.z <= 0) continue;
                float distance = ((Vector2)screen - cursor).sqrMagnitude;
                if (distance >= bestBadgeDistance) continue;
                bestBadgeDistance = distance;
                selected = rider;
            }
            if (selected != null) return true;

            // A body can still overlap another body. When that happens, first
            // favour somebody who must exit at this floor, then a waiting rider
            // who can board, and only then the nearest remaining onboard rider.
            Rider exitHere = null, waiting = null, nearest = null;
            float exitDistance = float.MaxValue, waitingDistance = float.MaxValue, nearestDistance = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(ray, 100f))
            {
                if (!riderHits.TryGetValue(hit.collider, out Rider rider) || rider.Resolved) continue;
                if (waitingOnly && rider.Boarded) continue;
                if (hit.distance < nearestDistance) { nearestDistance = hit.distance; nearest = rider; }
                if (rider.Boarded && rider.Destination == round.Floor && hit.distance < exitDistance)
                { exitDistance = hit.distance; exitHere = rider; }
                if (!rider.Boarded && hit.distance < waitingDistance)
                { waitingDistance = hit.distance; waiting = rider; }
            }
            selected = exitHere ?? waiting ?? nearest;
            return selected != null;
        }

        void HandleWorldInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || eye == null) return;
            Vector2 cursor = mouse.position.ReadValue();
            Ray ray = eye.ScreenPointToRay(cursor);

            if (draggedRider != null)
            {
                UpdateRiderDrag(cursor);
                if (mouse.leftButton.wasReleasedThisFrame) FinishRiderDrag(cursor);
                return;
            }

            // Inspect the entire ray instead of only its nearest collider. A
            // passenger may stand in front of the wall panel, but a visible
            // elevator button should still receive the click beneath them.
            RaycastHit[] hits = Physics.RaycastAll(ray, 100f);
            if (hits.Length == 0) return;
            Collider openHit = null, closeHit = null, floorHit = null;
            float openDistance = float.MaxValue, closeDistance = float.MaxValue, floorDistance = float.MaxValue;
            int floor = -1;
            foreach (RaycastHit candidate in hits)
            {
                if (openButtons.Contains(candidate.collider) && candidate.distance < openDistance)
                { openHit = candidate.collider; openDistance = candidate.distance; }
                if (closeButtons.Contains(candidate.collider) && candidate.distance < closeDistance)
                { closeHit = candidate.collider; closeDistance = candidate.distance; }
                if (floorButtons.TryGetValue(candidate.collider, out int candidateFloor) && candidate.distance < floorDistance)
                { floorHit = candidate.collider; floorDistance = candidate.distance; floor = candidateFloor; }
            }

            bool overOpen = openHit != null;
            if (overOpen && mouse.leftButton.isPressed)
            {
                if (phase == Phase.Closing) { phase = Phase.Opening; phaseTime = 0; }
                if (phase == Phase.Closed) { phase = Phase.Opening; phaseTime = 0; }
                if (phase == Phase.Boarding) { phaseTime = 0; round.HoldDoor(Time.deltaTime); }
            }

            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (floorHit != null)
                {
                    DepartToFloor(floor);
                    return;
                }
                if (closeHit != null)
                {
                    if (phase == Phase.Boarding)
                    {
                        if (selectedFloor >= 0) DepartToFloor(selectedFloor);
                        else CloseWithoutDestination();
                    }
                    return;
                }
                if (overOpen) return;
                bool canRearrange = phase == Phase.Boarding || phase == Phase.Closing
                    || phase == Phase.Closed || phase == Phase.Moving;
                if (canRearrange && TryPickRider(cursor, ray, false, out var rider)
                    && (rider.Boarded || phase == Phase.Boarding))
                {
                    BeginRiderDrag(rider, cursor);
                    return;
                }
            }
            if (mouse.rightButton.wasPressedThisFrame && phase == Phase.Boarding && TryPickRider(cursor, ray, true, out var rejected))
            {
                if (round.Reject(rejected)) { phaseTime = 0; SetControlStatus("PASSED"); Play(buzz); }
            }
        }

        void StartOrContinue()
        {
            if (phase == Phase.Welcome && !tutorialSeen)
            {
                tutorialSeen = true; phase = Phase.Tutorial; phaseTime = 0; Play(chime);
                return;
            }
            Restart();
        }

        void Restart()
        {
            foreach (var f in figures.Values) Destroy(f.gameObject); figures.Clear();
            bubbles.Clear(); destinationTags.Clear(); cabinPositions.Clear(); exiting.Clear(); exitStarts.Clear(); riderHits.Clear();
            draggedRider = null;
            round = new ElevatorRound(); phase = Phase.Boarding; paused = false; phaseTime = 0; doors = 1;
            leftDoor.gameObject.SetActive(false); rightDoor.gameObject.SetActive(false);
            floorSign.text = "00"; floorSign.transform.localPosition = floorSignHome; floorSign.characterSize = .04f;
            arrivalImpact = 0; ResetCameraMotion(); selectedFloor = -1; destination = -1;
            RefreshFloorButtons(); SetControlStatus("READY"); notice = "Drag passengers into any clear floor space, then choose a floor.";
            SyncFigures(0); Play(chime);
        }

        void Styles()
        {
            if (body != null) return;
            body = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = false };
            body.normal.textColor = Cream;
            small = new GUIStyle(body) { fontSize = 12 };
            title = new GUIStyle(body) { fontSize = 36, fontStyle = FontStyle.Bold };
            large = new GUIStyle(body) { fontSize = 24, fontStyle = FontStyle.Bold };
            buttonStyle = new GUIStyle(body) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            inkBody = new GUIStyle(body); inkBody.normal.textColor = Ink;
            inkSmall = new GUIStyle(small); inkSmall.normal.textColor = Ink;
            inkLarge = new GUIStyle(large); inkLarge.normal.textColor = Ink;
            logo = new GUIStyle(body) { fontSize = 96, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            logo.normal.textColor = Cream;
            stampWord = new GUIStyle(body) { fontSize = 62, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Overflow };
            stampWord.normal.textColor = Coral;
        }
        void Panel(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old; }
        void Label(Rect r, string text, GUIStyle style = null) => GUI.Label(r, text, style ?? body);
        bool Button(Rect r, string text, Color color, bool enabled = true)
        {
            bool was = GUI.enabled; GUI.enabled = was && enabled;
            Panel(r, GUI.enabled ? color : new Color(.2f, .25f, .3f));
            var old = buttonStyle.normal.textColor; buttonStyle.normal.textColor = GUI.enabled ? Ink : Color.gray;
            bool pressed = GUI.Button(r, text, buttonStyle); buttonStyle.normal.textColor = old; GUI.enabled = was; return pressed;
        }

        string ShortRequest(Rider p)
        {
            if (p.Kind == "COURIER") return "2 spaces • 1 floor";
            if (p.Kind == "PREGNANT") return "2 spaces";
            if (p.Kind == "INTERVIEW") return "Very urgent";
            if (p.Kind == "BOSS") return "Hold OPEN bonus";
            if (p.Kind == "ELDERLY") return "Slow • big bonus";
            return "3 spaces together";
        }

        void OnGUI()
        {
            if (round == null || eye == null) return;
            Styles();
            eye.pixelRect = new Rect(0, 0, Screen.width, Screen.height);
            if (phase == Phase.Intro)
            {
                scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
                offsetX = (Screen.width - 1440 * scale) / 2; offsetY = (Screen.height - 900 * scale) / 2;
                GUI.matrix = Matrix4x4.identity;
                if (introTime < IntroRevealStart)
                {
                    Panel(new Rect(0, 0, Screen.width, Screen.height), Color.black);
                }
                else
                {
                    // Mask only the two moving halves. The centre is deliberately
                    // left undrawn so the live 3D elevator is actually revealed.
                    float reveal = EaseOut(Mathf.Clamp01((introTime - IntroRevealStart) / IntroRevealDuration));
                    float curtainWidth = Screen.width * .5f * (1 - reveal);
                    Panel(new Rect(0, 0, curtainWidth, Screen.height), Color.black);
                    Panel(new Rect(Screen.width - curtainWidth, 0, curtainWidth, Screen.height), Color.black);
                    float edgeAlpha = 1f - Mathf.Clamp01(reveal * .85f);
                    Panel(new Rect(curtainWidth - 6, 0, 6, Screen.height), new Color(Cream.r, Cream.g, Cream.b, edgeAlpha));
                    Panel(new Rect(Screen.width - curtainWidth, 0, 6, Screen.height), new Color(Cream.r, Cream.g, Cream.b, edgeAlpha));
                }
                GUI.matrix = Matrix4x4.TRS(new Vector3(offsetX, offsetY, 0), Quaternion.identity, Vector3.one * scale);
                IntroOverlay();
                GUI.matrix = Matrix4x4.identity;
                return;
            }
            if (phase == Phase.Welcome || phase == Phase.Tutorial || phase == Phase.Results || paused)
            {
                scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
                offsetX = (Screen.width - 1440 * scale) / 2; offsetY = (Screen.height - 900 * scale) / 2;
                GUI.matrix = Matrix4x4.TRS(new Vector3(offsetX, offsetY, 0), Quaternion.identity, Vector3.one * scale);
                Overlay();
            }
            else if (phase == Phase.Opening || phase == Phase.Boarding || phase == Phase.Closing)
            {
                // The remaining time contracts symmetrically toward the centre,
                // echoing the physical elevator doors closing from both sides.
                float fill = phase == Phase.Opening ? 1f : phase == Phase.Boarding
                    ? 1f - Mathf.Clamp01(phaseTime / DoorHoldDurationAtCurrentFloor()) : 0f;
                float margin = Mathf.Clamp(Screen.width * .025f, 24f, 36f);
                float width = Screen.width - margin * 2f;
                Panel(new Rect(margin, 18f, width, 12f), new Color(Ink.r, Ink.g, Ink.b, .55f));
                float remaining = width * fill;
                Panel(new Rect(margin + (width - remaining) * .5f, 18f, remaining, 12f), fill > .25f ? Teal : Coral);
                Panel(new Rect(Screen.width * .5f - 1f, 16f, 2f, 16f), Cream);
            }
            GUI.matrix = Matrix4x4.identity;
        }

        void IntroOverlay()
        {
            float logoProgress = EaseOut(Mathf.Clamp01((introTime - .18f) / 1.05f));
            float logoY = Mathf.Lerp(-150, 328, logoProgress);
            float stampProgress = Mathf.Clamp01((introTime - 1.12f) / .24f);
            float wordAlpha = Mathf.Clamp01((introTime - 1.12f) / .18f);
            float fade = 1 - Mathf.Clamp01((introTime - 2.48f) / .57f);
            if (fade <= 0) return;
            Color markColor = new Color(Teal.r, Teal.g, Teal.b, fade);
            Panel(new Rect(650, logoY - 65, 140, 130), new Color(Ink.r, Ink.g, Ink.b, fade));
            Panel(new Rect(659, logoY - 54, 58, 108), markColor);
            Panel(new Rect(723, logoY - 54, 58, 108), markColor);
            Panel(new Rect(669, logoY - 43, 102, 86), new Color(45 / 255f, 58 / 255f, 97 / 255f, fade));
            // Draw the lift pictogram from primitives instead of relying on an
            // emoji font, which is not bundled consistently in standalone builds.
            Color cream = new Color(Cream.r, Cream.g, Cream.b, fade);
            Panel(new Rect(718, logoY - 43, 4, 86), cream);
            Panel(new Rect(684, logoY - 19, 13, 13), cream);
            Panel(new Rect(682, logoY - 3, 17, 31), cream);
            Panel(new Rect(743, logoY - 19, 13, 13), cream);
            Panel(new Rect(741, logoY - 3, 17, 31), cream);
            if (wordAlpha > 0)
            {
                Matrix4x4 oldMatrix = GUI.matrix;
                Vector2 stampPivot = new Vector2(620, logoY - 86);
                GUIUtility.RotateAroundPivot(-9f, stampPivot);
                float stampScale = 1.22f - .22f * EaseOut(stampProgress);
                GUIUtility.ScaleAroundPivot(new Vector2(stampScale, stampScale), stampPivot);
                GUI.color = new Color(0, 0, 0, wordAlpha * fade * .45f);
                DrawStampWord(430, logoY - 126);
                GUI.color = new Color(1, 1, 1, wordAlpha * fade);
                DrawStampWord(425, logoY - 132);
                GUI.matrix = oldMatrix;
                GUI.color = Color.white;
            }
        }

        void DrawStampWord(float x, float y)
        {
            const float letterWidth = 78;
            const string word = "CRAZY";
            for (int i = 0; i < word.Length; i++)
                Label(new Rect(x + i * letterWidth, y, letterWidth, 82), word[i].ToString(), stampWord);
        }

        void Overlay()
        {
            Panel(new Rect(0, 104, 1440, 796), new Color(0, .025f, .05f, .82f));
            Panel(new Rect(330, 222, 780, 456), Ink); Panel(new Rect(330, 222, 780, 5), Teal);
            string heading = paused ? "TAKE A BREATHER" : phase == Phase.Welcome ? "YOUR SHIFT. THEIR CHAOS." : phase == Phase.Tutorial ? "HOW TO PLAY" : round.Won ? "EMPLOYEE OF THE DREAM" : "A VERY CHAOTIC SHIFT";
            Label(new Rect(372, 256, 700, 52), heading, title);
            string copy;
            if (paused) copy = "The clock is paused.\n\nPress Escape or resume when you are ready.";
            else if (phase == Phase.Welcome) copy = "Reach the DREAM DECK as fast as you can. Deliver as many riders happily as possible on the way.\nDrag waiting riders into any clear cabin space. Rearrange riders sideways or toward the back.\n\nAt their floor, drag them back through the doorway. Missing a stop or using the wrong floor makes riders unhappy.\nHold OPEN for slow riders and the boss bonus.";
            else if (phase == Phase.Tutorial) copy = "Three things to know.\n\nRead the short badge above each rider: type first, destination second.";
            else copy = "Highest floor: " + round.PeakFloor.ToString("00") + " / " + (ElevatorRound.Floors - 1).ToString("00")
                + "\nHappy riders: " + round.Happy + "  •  Delivered: " + round.Delivered
                + "\nClimb time: " + round.PeakTime.ToString("0.0") + " sec  •  Score: " + round.Score
                + "\n\n" + (round.Won ? "You reached the Dream Deck!" : "Reach higher, faster, and keep the riders happy.");
            Label(new Rect(374, 322, 690, 240), copy, body);
            if (phase == Phase.Tutorial)
            {
                Panel(new Rect(374, 418, 210, 112), Teal); Panel(new Rect(602, 418, 210, 112), new Color32(255, 205, 82, 255)); Panel(new Rect(830, 418, 210, 112), Coral);
                Label(new Rect(392, 432, 174, 78), "1  DRAG IN\nUse any clear area\nof the cabin floor.", inkBody);
                Label(new Rect(620, 432, 174, 78), "2  ARRANGE\nMove riders sideways\nor toward the back.", inkBody);
                Label(new Rect(848, 432, 174, 78), "3  DRAG OUT\nAt their floor, pull\nthem through the door.", inkBody);
            }
            string action = paused ? "RESUME SHIFT" : phase == Phase.Welcome ? "SHOW ME HOW  /  ENTER" : phase == Phase.Tutorial ? "START SHIFT  /  ENTER" : "TRY AGAIN  /  ENTER";
            if (Button(new Rect(374, 600, 692, 48), action, Teal))
            { if (paused) paused = false; else StartOrContinue(); }
        }

        AudioClip Tone(float frequency, float seconds)
        {
            const int rate = 22050; var data = new float[(int)(rate * seconds)];
            for (int i = 0; i < data.Length; i++) { float t = i / (float)rate; data[i] = Mathf.Sin(2 * Mathf.PI * frequency * t) * Mathf.Sin(Mathf.PI * i / data.Length) * .5f; }
            var clip = AudioClip.Create("Elevator tone", data.Length, 1, rate, false); clip.SetData(data, 0); return clip;
        }

        AudioClip DingTone()
        {
            const int rate = 22050; const float seconds = 1.05f;
            var data = new float[Mathf.CeilToInt(rate * seconds)];
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)rate;
                float attack = Mathf.Clamp01(t / .008f);
                float decay = Mathf.Exp(-t * 4.6f);
                float bell = Mathf.Sin(2 * Mathf.PI * 880f * t) * .46f
                           + Mathf.Sin(2 * Mathf.PI * 1320f * t) * .22f
                           + Mathf.Sin(2 * Mathf.PI * 1760f * t) * .10f;
                data[i] = bell * attack * decay;
            }
            var clip = AudioClip.Create("Elevator arrival ding", data.Length, 1, rate, false);
            clip.SetData(data, 0); return clip;
        }

        AudioClip Groove()
        {
            // A tiny four-bar arcade loop: bass, kick, handclaps and a cheerful synth hook.
            // It is generated locally so the prototype has no external audio dependency.
            const int rate = 22050; const float bpm = 116; const int beats = 16;
            float beatLength = 60f / bpm, seconds = beats * beatLength;
            var data = new float[Mathf.CeilToInt(rate * seconds)];
            float[] bass = { 130.81f, 164.81f, 196f, 174.61f };
            float[] hook = { 523.25f, 659.25f, 783.99f, 659.25f, 587.33f, 698.46f, 880f, 698.46f };
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)rate, beat = t / beatLength, half = t % (beatLength * .5f);
                int beatIndex = Mathf.FloorToInt(beat) % beats, bar = beatIndex / 4;
                float beatPhase = t % beatLength, kick = Mathf.Exp(-beatPhase * 18f) * Mathf.Sin(2 * Mathf.PI * (84 - beatPhase * 35) * t) * .20f;
                float bassPhase = t % beatLength, bassLine = Mathf.Sin(2 * Mathf.PI * bass[bar] * t) * Mathf.Exp(-bassPhase * 3.8f) * .12f;
                float clapPhase = beatPhase;
                float noise = Mathf.Repeat(Mathf.Sin(i * 12.9898f) * 43758.5453f, 1f) * 2f - 1f;
                float clap = Mathf.Abs(noise) * Mathf.Exp(-clapPhase * 32f) * ((beatIndex % 4 == 1 || beatIndex % 4 == 3) ? .055f : 0);
                int hookIndex = Mathf.FloorToInt(t / (beatLength * .5f)) % hook.Length;
                float hookPhase = half, melody = Mathf.Sin(2 * Mathf.PI * hook[hookIndex] * t) * Mathf.Exp(-hookPhase * 8f) * .055f;
                float pad = Mathf.Sin(2 * Mathf.PI * bass[bar] * 2f * t) * .018f + Mathf.Sin(2 * Mathf.PI * bass[bar] * 3f * t) * .012f;
                float edge = Mathf.Min(1f, Mathf.Min(t / .035f, (seconds - t) / .035f));
                data[i] = Mathf.Clamp((kick + bassLine + clap + melody + pad) * edge, -.45f, .45f);
            }
            var clip = AudioClip.Create("Crazy Elevator Groove", data.Length, 1, rate, false); clip.SetData(data, 0); return clip;
        }

        AudioClip StampTone()
        {
            const int rate = 22050; const float seconds = .24f;
            var data = new float[Mathf.CeilToInt(rate * seconds)];
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)rate;
                float thump = Mathf.Sin(2 * Mathf.PI * (145 - 90 * t) * t) * Mathf.Exp(-t * 20f) * .55f;
                float noise = (Mathf.Repeat(Mathf.Sin(i * 91.7f) * 43758.5453f, 1f) * 2f - 1f) * Mathf.Exp(-t * 38f) * .22f;
                data[i] = Mathf.Clamp(thump + noise, -.8f, .8f);
            }
            var clip = AudioClip.Create("Crazy stamp", data.Length, 1, rate, false);
            clip.SetData(data, 0); return clip;
        }

        void Play(AudioClip sound) { if (speaker != null && sound != null) speaker.PlayOneShot(sound); }
        void OnDestroy()
        {
            foreach (var m in materials.Values) Destroy(m);
            if (chime) Destroy(chime); if (ding) Destroy(ding); if (click) Destroy(click); if (buzz) Destroy(buzz); if (groove) Destroy(groove); if (stamp) Destroy(stamp);
        }
    }
}
