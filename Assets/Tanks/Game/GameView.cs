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
        private Transform[] _tankRoots;   // empty parents carrying the body's facing
        private Transform[] _tankBarrels;
        private GameObject[] _bullets;

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

                if (!t.Alive)
                {
                    root.gameObject.SetActive(false);
                    barrel.gameObject.SetActive(false);
                    continue;
                }
                root.gameObject.SetActive(true);
                barrel.gameObject.SetActive(true);

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

                _tankRoots[i] = root;
                _tankBarrels[i] = barrel;
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
    }
}
