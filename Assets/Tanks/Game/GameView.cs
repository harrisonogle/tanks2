using System.Collections.Generic;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Renders the simulation state with crude primitives. This is a pure "view": it reads
    /// <see cref="SimRunner.State"/> and positions GameObjects. It never writes to the sim.
    ///
    /// Sim space is 2D (X, Y) in [0,Width] x [0,Height]; we map it onto the world XZ plane.
    /// No interpolation yet — it snaps to the latest tick (fine at 60 Hz). Interpolating
    /// between the two most recent ticks is an easy later polish.
    /// </summary>
    public sealed class GameView : MonoBehaviour
    {
        private static readonly Color P0Color = new Color(0.30f, 0.55f, 1.00f);
        private static readonly Color P1Color = new Color(1.00f, 0.40f, 0.35f);
        private static readonly Color WallColor = new Color(0.45f, 0.45f, 0.50f);
        private static readonly Color FloorColor = new Color(0.14f, 0.15f, 0.18f);
        private static readonly Color BulletColor = new Color(1.00f, 0.95f, 0.55f);
        private static readonly Color BarrelColor = new Color(0.90f, 0.90f, 0.95f);
        private static readonly Color FrontMarkerColor = new Color(0.10f, 0.10f, 0.12f);

        private SimRunner _runner;
        private Transform[] _tankRoots;       // empty parents carrying the body's facing
        private Transform[] _tankBarrels;
        private LineRenderer[] _losLines;     // per-tank line-of-sight predictor (visual only)
        private readonly List<Vector3> _losBuffer = new List<Vector3>();
        private GameObject[] _bullets;

        // Line-of-sight rendering tuning.
        private const float LosLineY = 0.3f;     // world Y of the trajectory line (matches bullet render Y)
        private const float LosLineWidth = 0.04f;
        private const float LosLineAlpha = 0.55f;
        private const int LosMaxSteps = 400;     // safety cap on trace iterations (arena fits easily)

        private void Start()
        {
            _runner = GetComponent<SimRunner>();
            BuildArena();
            BuildTanks();
            BuildBullets();
        }

        private void LateUpdate()
        {
            var state = _runner.State;
            if (state == null) return;

            for (int i = 0; i < SimConfig.PlayerCount; i++)
            {
                ref readonly Tank t = ref state.Tanks[i];
                var root = _tankRoots[i];
                var barrel = _tankBarrels[i];
                var losLine = _losLines[i];

                if (!t.Alive)
                {
                    root.gameObject.SetActive(false);
                    barrel.gameObject.SetActive(false);
                    losLine.enabled = false;
                    continue;
                }
                root.gameObject.SetActive(true);
                barrel.gameObject.SetActive(true);
                losLine.enabled = true;

                // Tank root carries the body's facing rotation; the visible cube and the small
                // front orientation marker are children of root, so they share the rotation
                // without us having to position each one individually each frame.
                root.position = ToWorld(t.X, t.Y, 0.3f);
                float bc = Trig.Cos(t.Angle).ToFloat();
                float bs = Trig.Sin(t.Angle).ToFloat();
                root.rotation = Quaternion.LookRotation(new Vector3(bc, 0f, bs), Vector3.up);

                // Barrel: position + rotation from TURRET angle (fire direction, independent
                // of body). Offset forward by ~half a barrel length so it visibly sticks out.
                float tc = Trig.Cos(t.TurretAngle).ToFloat();
                float ts = Trig.Sin(t.TurretAngle).ToFloat();
                Vector3 turretDir = new Vector3(tc, 0f, ts);
                Vector3 bodyPos = ToWorld(t.X, t.Y, 0.35f);
                barrel.position = bodyPos + turretDir * 0.55f;
                barrel.rotation = Quaternion.LookRotation(turretDir, Vector3.up);

                // Line of sight: trace the would-be shell path from the muzzle through any
                // ricochets, terminating where the shell would detonate (wall, opponent tank,
                // or the owner post-ricochet). Refreshed each frame.
                _losBuffer.Clear();
                TraceLoS(state, i, _runner.Arena, _losBuffer);
                losLine.positionCount = _losBuffer.Count;
                for (int k = 0; k < _losBuffer.Count; k++)
                    losLine.SetPosition(k, _losBuffer[k]);
            }

            for (int i = 0; i < state.Bullets.Length; i++)
            {
                ref readonly Bullet b = ref state.Bullets[i];
                var go = _bullets[i];
                if (!b.Active) { go.SetActive(false); continue; }
                go.SetActive(true);
                go.transform.position = ToWorld(b.X, b.Y, 0.3f);
            }
        }

        private static Vector3 ToWorld(Fixed x, Fixed y, float worldY)
            => new Vector3(x.ToFloat(), worldY, y.ToFloat());

        private void BuildArena()
        {
            float w = _runner.Arena.Width.ToFloat();
            float h = _runner.Arena.Height.ToFloat();

            // Floor
            var floor = CreateBox("Floor", FloorColor);
            floor.position = new Vector3(w / 2f, -0.05f, h / 2f);
            floor.localScale = new Vector3(w, 0.1f, h);

            // Border walls (visual; the sim reflects/clamps at the bounds implicitly).
            const float tBorder = 0.3f, hBorder = 1f;
            MakeBorder("Border-S", new Vector3(w / 2f, hBorder / 2f, 0f), new Vector3(w, hBorder, tBorder));
            MakeBorder("Border-N", new Vector3(w / 2f, hBorder / 2f, h), new Vector3(w, hBorder, tBorder));
            MakeBorder("Border-W", new Vector3(0f, hBorder / 2f, h / 2f), new Vector3(tBorder, hBorder, h));
            MakeBorder("Border-E", new Vector3(w, hBorder / 2f, h / 2f), new Vector3(tBorder, hBorder, h));

            // Interior wall blocks from the arena definition.
            foreach (var wall in _runner.Arena.Walls)
            {
                var box = CreateBox("Wall", WallColor);
                float cx = wall.CenterX.ToFloat();
                float cz = wall.CenterY.ToFloat();
                float sx = (wall.MaxX - wall.MinX).ToFloat();
                float sz = (wall.MaxY - wall.MinY).ToFloat();
                box.position = new Vector3(cx, 0.5f, cz);
                box.localScale = new Vector3(sx, 1f, sz);
            }
        }

        private void MakeBorder(string name, Vector3 pos, Vector3 scale)
        {
            var b = CreateBox(name, WallColor);
            b.position = pos;
            b.localScale = scale;
        }

        private void BuildTanks()
        {
            float d = SimConfig.TankRadius.ToFloat() * 2f;
            _tankRoots = new Transform[SimConfig.PlayerCount];
            _tankBarrels = new Transform[SimConfig.PlayerCount];
            _losLines = new LineRenderer[SimConfig.PlayerCount];
            var losShader = Shader.Find("Sprites/Default"); // unlit, alpha-blended, built-in
            for (int i = 0; i < SimConfig.PlayerCount; i++)
            {
                // Empty root carries the body's facing; the body cube and the front marker hang
                // off it. The root stays unscaled so each child's scale lives in clean units.
                var root = new GameObject($"Tank{i}").transform;

                // Visible body cube (matches the sim's collision footprint exactly).
                var body = CreateBox($"Tank{i}-Body", i == 0 ? P0Color : P1Color);
                body.SetParent(root, worldPositionStays: false);
                body.localScale = new Vector3(d, 0.6f, d);

                // Small dark marker sitting on top of the body's front edge — visual cue for
                // body facing. In root-local space: body top is at Y=0.3, body front at Z=0.6;
                // the marker (scale 0.4, 0.15, 0.3) is centered at (0, 0.375, 0.45) so its
                // bottom touches the body's top face and its front aligns with the body's front.
                var marker = CreateBox($"Tank{i}-Marker", FrontMarkerColor);
                marker.SetParent(root, worldPositionStays: false);
                marker.localScale = new Vector3(0.4f, 0.15f, 0.3f);
                marker.localPosition = new Vector3(0f, 0.375f, 0.45f);

                // Barrel: SIBLING of root (not a child), since it rotates with TurretAngle
                // independently. Its world transform is set each frame in LateUpdate.
                var barrel = CreateBox($"Tank{i}-Barrel", BarrelColor);
                barrel.localScale = new Vector3(0.18f, 0.18f, 0.9f);

                // Line of sight: a thin world-space line showing where the shell would go if
                // fired right now (through ricochets, terminating where it would detonate).
                // Pure visual — Sim/Net layers don't know it exists.
                var losGo = new GameObject($"Tank{i}-LoS");
                var lr = losGo.AddComponent<LineRenderer>();
                lr.material = new Material(losShader);
                Color baseColor = i == 0 ? P0Color : P1Color;
                Color losColor = new Color(baseColor.r, baseColor.g, baseColor.b, LosLineAlpha);
                lr.startColor = losColor;
                lr.endColor = losColor;
                lr.startWidth = LosLineWidth;
                lr.endWidth = LosLineWidth;
                lr.useWorldSpace = true;
                lr.positionCount = 0;

                _tankRoots[i] = root;
                _tankBarrels[i] = barrel;
                _losLines[i] = lr;
            }
        }

        private void BuildBullets()
        {
            float d = SimConfig.BulletRadius.ToFloat() * 2.4f;
            _bullets = new GameObject[SimConfig.MaxBullets];
            for (int i = 0; i < SimConfig.MaxBullets; i++)
            {
                var go = CreateSphere($"Bullet{i}", BulletColor);
                go.localScale = new Vector3(d, d, d);
                go.gameObject.SetActive(false);
                _bullets[i] = go.gameObject;
            }
        }

        private static Transform CreateBox(string name, Color color)
            => CreatePrimitive(PrimitiveType.Cube, name, color);

        private static Transform CreateSphere(string name, Color color)
            => CreatePrimitive(PrimitiveType.Sphere, name, color);

        private static Transform CreatePrimitive(PrimitiveType type, string name, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            // We do our own collision in the sim; drop the auto-added collider.
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.GetComponent<Renderer>().material.color = color;
            return go.transform;
        }

        /// <summary>
        /// Trace the trajectory a freshly-fired shell from <paramref name="ownerIndex"/> would
        /// follow, stopping at the FIRST wall contact (no ricochets in the preview) or the first
        /// opponent-tank hit. Deliberately a strictly-shorter, easier-to-read guide than the full
        /// physics path. Pure rendering math (floats). Output polyline: muzzle, terminus.
        /// </summary>
        private static void TraceLoS(GameState state, int ownerIndex, Arena arena, List<Vector3> output)
        {
            ref readonly Tank owner = ref state.Tanks[ownerIndex];

            float c = Trig.Cos(owner.TurretAngle).ToFloat();
            float s = Trig.Sin(owner.TurretAngle).ToFloat();
            float muzzleOffset = SimConfig.TankRadius.ToFloat() + SimConfig.BulletRadius.ToFloat() + 0.05f;
            float px = owner.X.ToFloat() + c * muzzleOffset;
            float py = owner.Y.ToFloat() + s * muzzleOffset;
            float speed = SimConfig.BulletSpeed.ToFloat();
            float vx = c * speed;
            float vy = s * speed;
            float arenaW = arena.Width.ToFloat();
            float arenaH = arena.Height.ToFloat();
            float reach = SimConfig.TankRadius.ToFloat() + SimConfig.BulletRadius.ToFloat();

            output.Add(new Vector3(px, LosLineY, py));

            for (int step = 0; step < LosMaxSteps; step++)
            {
                float nx = px + vx;
                float ny = py + vy;

                // Arena bounds — terminate at the contact bound (no reflection in the preview).
                if (nx < 0f)     { output.Add(new Vector3(0f,      LosLineY, ny)); return; }
                if (nx > arenaW) { output.Add(new Vector3(arenaW,  LosLineY, ny)); return; }
                if (ny < 0f)     { output.Add(new Vector3(nx,      LosLineY, 0f)); return; }
                if (ny > arenaH) { output.Add(new Vector3(nx,      LosLineY, arenaH)); return; }

                // Interior wall blocks — terminate at the shallowest-penetration face.
                foreach (var w in arena.Walls)
                {
                    float minX = w.MinX.ToFloat();
                    float maxX = w.MaxX.ToFloat();
                    float minY = w.MinY.ToFloat();
                    float maxY = w.MaxY.ToFloat();
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;

                    float distLeft = nx - minX;
                    float distRight = maxX - nx;
                    float distBottom = ny - minY;
                    float distTop = maxY - ny;
                    bool exitX = Mathf.Min(distLeft, distRight) < Mathf.Min(distBottom, distTop);
                    if (exitX) nx = distLeft < distRight ? minX : maxX;
                    else       ny = distBottom < distTop ? minY : maxY;
                    output.Add(new Vector3(nx, LosLineY, ny));
                    return;
                }

                // Tank-hit check — terminate where the shell would land on a valid target. The
                // owner is always pre-ricochet here (no ricochets in this preview), so it's
                // permanently immune for the line's purposes.
                for (int j = 0; j < SimConfig.PlayerCount; j++)
                {
                    if (j == ownerIndex) continue;
                    ref readonly Tank t = ref state.Tanks[j];
                    if (!t.Alive) continue;
                    float tx = t.X.ToFloat();
                    float ty = t.Y.ToFloat();
                    if (Mathf.Abs(nx - tx) <= reach && Mathf.Abs(ny - ty) <= reach)
                    {
                        output.Add(new Vector3(nx, LosLineY, ny));
                        return;
                    }
                }

                px = nx;
                py = ny;
            }

            // Safety cap reached (shouldn't happen with arena bounds always present); soft end.
            output.Add(new Vector3(px, LosLineY, py));
        }
    }
}
