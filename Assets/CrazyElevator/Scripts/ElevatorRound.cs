using System;
using System.Collections.Generic;

namespace CrazyElevator
{
    // Pure game rules; independent of scene objects and frame rate.
    public sealed class Rider
    {
        public string Name, Request, Kind, Badge, Status;
        public int Origin, Destination, Space, Color, Bonus, Mood = 2;
        public float Patience, Remaining, Arrival, HoldRequired, HoldProgress;
        public bool Boarded, Resolved;
        public bool HoldSatisfied => HoldRequired <= 0 || HoldProgress >= HoldRequired;
    }

    public enum OffboardResult { None, Happy, Late, WrongFloor }

    public sealed class ElevatorRound
    {
        public const int Floors = 12;
        public const float Duration = 140f;
        public readonly List<Rider> Riders = new List<Rider>();
        public int Floor, PeakFloor, Score, Delivered, Happy, Missed, TurnedAway;
        public float PeakTime;
        public float TimeLeft = Duration;
        public int Load { get { int n = 0; foreach (var p in Riders) if (p.Boarded && !p.Resolved) n += p.Space; return n; } }
        public bool Finished => TimeLeft <= 0 || Riders.TrueForAll(p => p.Resolved);
        public bool Won => PeakFloor >= Floors - 1;

        public ElevatorRound()
        {
            for (int f = 0; f < Floors; f++)
            {
                Add("Remy", "COURIER", "Two spaces. Quick stop!", "BOX", f, (f + 1) % Floors, 2, 34, 0, 0, 30);
                Add("Mina", "PREGNANT", "Two spaces, please.", "2X", f, (f + 2) % Floors, 2, 42, 0, 1, 70);
                Add("Jules", "INTERVIEW", "My interview starts soon!", "!", f, (f + 3) % Floors, 1, 13, 0, 2, 120);
                Add("Morgan", "BOSS", "Hold OPEN for my bonus.", "B", f, (f + 4) % Floors, 1, 30, 0, 3, 140, 1.25f);
                Add("Eli", "ELDERLY", "Please wait for me...", "SLOW", f, (f + 2) % Floors, 1, 58, 6.2f, 4, 175);
                Add("The Trio", "GROUP", "All three or none!", "3X", f, (f + 1) % Floors, 3, 32, 0, 5, 130);
            }
        }

        void Add(string name, string kind, string request, string badge, int from, int to, int space,
            float patience, float arrival, int color, int bonus, float holdRequired = 0)
        {
            Riders.Add(new Rider { Name = name, Kind = kind, Request = request, Origin = from,
                Destination = to, Space = space, Patience = patience, Remaining = patience, Arrival = arrival,
                Color = color, Badge = badge, Bonus = bonus, HoldRequired = holdRequired });
        }

        public bool Board(Rider p)
        {
            // Physical placement in the 3D cabin decides whether somebody fits;
            // the rules layer only validates that this rider may board now.
            if (Finished || p.Resolved || p.Boarded || p.Origin != Floor || !IsOffered(p) || p.Arrival > 0) return false;
            p.Boarded = true;
            return true;
        }

        public bool Reject(Rider p)
        {
            if (Finished || p.Resolved || p.Boarded || p.Origin != Floor || !IsOffered(p)) return false;
            p.Resolved = true; TurnedAway++; Score -= 15; return true;
        }

        public bool IsOffered(Rider candidate)
        {
            if (candidate == null || candidate.Resolved || candidate.Boarded || candidate.Origin != Floor) return false;
            int offered = 0;
            foreach (var p in Riders)
            {
                if (p.Resolved || p.Boarded || p.Origin != Floor) continue;
                if (p == candidate) return offered < 3;
                offered++;
            }
            return false;
        }

        public bool Remove(Rider p)
        {
            if (Finished || !p.Boarded || p.Resolved) return false;
            p.Boarded = false; p.Resolved = true; TurnedAway++; Score -= 40; return true;
        }

        public void HoldDoor(float dt)
        {
            if (dt <= 0) return;
            foreach (var p in Riders)
            {
                if (!p.Boarded || p.Resolved || p.HoldRequired <= 0 || p.HoldSatisfied) continue;
                p.HoldProgress = Math.Min(p.HoldRequired, p.HoldProgress + dt);
                if (p.HoldSatisfied) p.Status = "Boss bonus ready!";
            }
        }

        // Called only when the car truly leaves, so reopening a closing door
        // still lets the player rescue somebody at their requested floor.
        public int LeaveFloor()
        {
            int missedHere = 0;
            foreach (var p in Riders)
            {
                if (!p.Boarded || p.Resolved || p.Destination != Floor) continue;
                p.Mood = Math.Max(0, p.Mood - 1);
                p.Status = "You passed my floor!";
                Score -= 35; missedHere++;
            }
            return missedHere;
        }

        public void Tick(float dt, bool stopped)
        {
            if (Finished || dt <= 0) return;
            TimeLeft = Math.Max(0, TimeLeft - dt);
            foreach (var p in Riders)
            {
                if (p.Resolved) continue;
                if (p.Boarded) p.Remaining = Math.Max(0, p.Remaining - dt);
                else if (stopped && p.Origin == Floor && IsOffered(p))
                {
                    float waiting = dt;
                    if (p.Arrival > 0) { waiting = Math.Max(0, dt - p.Arrival); p.Arrival = Math.Max(0, p.Arrival - dt); }
                    p.Remaining = Math.Max(0, p.Remaining - waiting);
                    if (p.Remaining <= 0) { p.Resolved = true; Missed++; Score -= 20; }
                }
            }
        }

        // Arrival only opens the doors. Delivery is deliberately manual: the
        // player must click each onboard party to let them out.
        public int Arrive(int floor)
        {
            if (floor < 0 || floor >= Floors) throw new ArgumentOutOfRangeException(nameof(floor));
            Floor = floor;
            if (floor > PeakFloor)
            {
                PeakFloor = floor;
                PeakTime = Duration - TimeLeft;
            }
            int waitingToExit = 0;
            foreach (var p in Riders)
            {
                if (p.Boarded && !p.Resolved && p.Destination == floor) waitingToExit++;
            }
            return waitingToExit;
        }

        public OffboardResult Offboard(Rider p)
        {
            if (Finished || p == null || !p.Boarded || p.Resolved) return OffboardResult.None;
            p.Boarded = false; p.Resolved = true; Delivered++;
            if (p.Destination != Floor)
            {
                p.Mood = 0; p.Status = "Wrong floor!"; Score -= 60;
                return OffboardResult.WrongFloor;
            }

            bool onTime = p.Remaining > 0;
            int reward = onTime ? 100 + (int)(50 * p.Remaining / p.Patience) : 25;
            if (p.Mood == 1) reward /= 2;
            if (onTime && p.Mood > 0)
            {
                Happy++;
                if (p.HoldSatisfied) reward += p.Bonus;
            }
            Score += reward;
            p.Status = onTime && p.Mood > 0 ? "Made it!" : "Finally...";
            return onTime && p.Mood > 0 ? OffboardResult.Happy : OffboardResult.Late;
        }
    }
}
