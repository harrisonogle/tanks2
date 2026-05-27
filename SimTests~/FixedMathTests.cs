using NUnit.Framework;
using Tankz.Sim;

namespace Tankz.Tests
{
    public class FixedMathTests
    {
        [Test]
        public void AddSubtract()
        {
            Assert.That(Fixed.FromInt(2) + Fixed.FromInt(3), Is.EqualTo(Fixed.FromInt(5)));
            Assert.That(Fixed.FromInt(5) - Fixed.FromInt(3), Is.EqualTo(Fixed.FromInt(2)));
            Assert.That(-Fixed.FromInt(4), Is.EqualTo(Fixed.FromInt(-4)));
        }

        [Test]
        public void MultiplyDivide()
        {
            Assert.That(Fixed.FromInt(3) * Fixed.FromInt(4), Is.EqualTo(Fixed.FromInt(12)));
            Assert.That(Fixed.FromInt(12) / Fixed.FromInt(4), Is.EqualTo(Fixed.FromInt(3)));
            Assert.That(Fixed.Half * Fixed.Half, Is.EqualTo(Fixed.FromFloat(0.25f)));
        }

        [Test]
        public void SqrtPerfectSquareIsExact()
        {
            Assert.That(Fixed.Sqrt(Fixed.FromInt(16)), Is.EqualTo(Fixed.FromInt(4)));
            Assert.That(Fixed.Sqrt(Fixed.FromInt(144)), Is.EqualTo(Fixed.FromInt(12)));
        }

        [Test]
        public void SqrtApproximatesIrrational()
        {
            // sqrt(2) ~= 1.41421
            float v = Fixed.Sqrt(Fixed.FromInt(2)).ToFloat();
            Assert.That(v, Is.EqualTo(1.41421f).Within(0.001f));
        }

        [Test]
        public void Comparisons()
        {
            Assert.That(Fixed.FromInt(1) < Fixed.FromInt(2), Is.True);
            Assert.That(Fixed.FromInt(2) > Fixed.FromInt(1), Is.True);
            Assert.That(Fixed.Clamp(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(3)), Is.EqualTo(Fixed.FromInt(3)));
        }

        [Test]
        public void DirectionIsUnitLength()
        {
            // Every lookup-table direction should be ~1 unit long.
            for (int a = 0; a < Trig.AngleCount; a += 37)
            {
                var d = Trig.Direction(a);
                float len = d.Length().ToFloat();
                Assert.That(len, Is.EqualTo(1f).Within(0.01f), $"angle {a}");
            }
        }
    }
}
