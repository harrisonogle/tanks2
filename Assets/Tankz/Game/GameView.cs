using Tankz.Sim;
using UnityEngine;

namespace Tankz.Game
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

        private SimRunner _runner;
        private Transform[] _tanks;
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
                var tr = _tanks[i];
                if (!t.Alive) { tr.gameObject.SetActive(false); continue; }

                tr.gameObject.SetActive(true);
                tr.position = ToWorld(t.X, t.Y, 0.3f);
                float c = Trig.Cos(t.Angle).ToFloat();
                float s = Trig.Sin(t.Angle).ToFloat();
                tr.rotation = Quaternion.LookRotation(new Vector3(c, 0f, s), Vector3.up);
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
            _tanks = new Transform[SimConfig.PlayerCount];
            for (int i = 0; i < SimConfig.PlayerCount; i++)
            {
                var body = CreateBox($"Tank{i}", i == 0 ? P0Color : P1Color);
                body.localScale = new Vector3(d, 0.6f, d);

                // Barrel: a child pointing +Z (forward). LookRotation aims the body's +Z
                // along the facing direction, so the barrel always points where the tank aims.
                var barrel = CreateBox($"Tank{i}-Barrel", BarrelColor);
                barrel.SetParent(body, worldPositionStays: false);
                barrel.localScale = new Vector3(0.18f, 0.18f, 0.9f);
                barrel.localPosition = new Vector3(0f, 0f, 0.55f);

                _tanks[i] = body;
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
