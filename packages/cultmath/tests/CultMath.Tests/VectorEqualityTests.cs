using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

// Hand-verified on hands/adopt-stryker: weakening float3.Equals from `&&` to `||` passes the
// whole existing suite. The same shape of Equals/GetHashCode, ==/!=, and </>/<=/>= exists (with
// no shared base type) across float2/3/4, double2/3, int2/3/4, bool2/3/4, quaternion,
// float2x2 and float3x3. These tests are data-driven per component position rather than one
// method per type: each type supplies a base value and, per component index, a value that
// differs at exactly that index and agrees everywhere else, plus (for the four ordered numeric
// families) a left/right pair whose relational result differs per component. bool2/3/4 and
// quaternion have no ordering operators and are not included in the comparison-operator cases.
// float2x2/float3x3 have no == / != operators (Equals and GetHashCode only) and are covered
// separately, at row granularity.
public sealed class VectorEqualityTests
{
    // ---- shared, type-agnostic assertions -------------------------------------------------

    private static void AssertEqualsAndHashCodeContract<T>(T a, T b, bool expectedEqual, string label)
        where T : IEquatable<T>
    {
        Assert.True(a.Equals(b) == expectedEqual, $"{label}: a.Equals(b) expected {expectedEqual}");
        Assert.True(b.Equals(a) == expectedEqual, $"{label}: b.Equals(a) expected {expectedEqual} (symmetry)");
        if (expectedEqual)
        {
            Assert.True(a.GetHashCode() == b.GetHashCode(), $"{label}: equal values must share a hash code");
        }
    }

    private static void AssertElementwiseEqualityOperators<TLeft, TRight, TBool>(
        TLeft a,
        TRight b,
        int count,
        Func<int, bool> expectedEqualAt,
        Func<TLeft, TRight, TBool> eq,
        Func<TLeft, TRight, TBool> neq,
        Func<TBool, int, bool> at,
        string label)
    {
        var eqResult = eq(a, b);
        var neqResult = neq(a, b);
        for (var i = 0; i < count; i++)
        {
            var expectedEqual = expectedEqualAt(i);
            Assert.True(at(eqResult, i) == expectedEqual, $"{label}: == component {i} expected {expectedEqual}");
            Assert.True(at(neqResult, i) == !expectedEqual, $"{label}: != component {i} expected {!expectedEqual}");
        }
    }

    private static void AssertElementwiseComparisonOperators<TLeft, TRight, TBool>(
        TLeft left,
        TRight right,
        double[] leftVals,
        double[] rightVals,
        Func<TLeft, TRight, TBool> lt,
        Func<TLeft, TRight, TBool> gt,
        Func<TLeft, TRight, TBool> le,
        Func<TLeft, TRight, TBool> ge,
        Func<TBool, int, bool> at,
        string label)
    {
        var ltResult = lt(left, right);
        var gtResult = gt(left, right);
        var leResult = le(left, right);
        var geResult = ge(left, right);
        for (var i = 0; i < leftVals.Length; i++)
        {
            var less = leftVals[i] < rightVals[i];
            var greater = leftVals[i] > rightVals[i];
            Assert.True(at(ltResult, i) == less, $"{label}: < component {i} (left={leftVals[i]}, right={rightVals[i]})");
            Assert.True(at(gtResult, i) == greater, $"{label}: > component {i} (left={leftVals[i]}, right={rightVals[i]})");
            Assert.True(at(leResult, i) == !greater, $"{label}: <= component {i} (left={leftVals[i]}, right={rightVals[i]})");
            Assert.True(at(geResult, i) == !less, $"{label}: >= component {i} (left={leftVals[i]}, right={rightVals[i]})");
        }
    }

    // ---- float2 -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Float2EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new float2(3.0f, -7.0f);
        var b = a;
        b[index] += 5.0f;

        AssertEqualsAndHashCodeContract(a, a, true, "float2 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"float2 differing at {index}");
        AssertElementwiseEqualityOperators<float2, float2, bool2>(
            a, b, 2, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float2 vector==vector");

        // Scalar overloads: a scalar equal to exactly one component, on both operand sides.
        var scalar = a[index];
        AssertElementwiseEqualityOperators<float2, float, bool2>(
            a, scalar, 2, i => a[i] == scalar, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float2 vector==scalar");
        AssertElementwiseEqualityOperators<float, float2, bool2>(
            scalar, a, 2, i => a[i] == scalar, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float2 scalar==vector");
    }

    [Fact]
    public void Float2ComparisonOperatorsDifferPerComponent()
    {
        var left = new float2(1.0f, 5.0f);
        var right = new float2(2.0f, 3.0f);
        var leftVals = new double[] { 1.0, 5.0 };
        var rightVals = new double[] { 2.0, 3.0 };

        AssertElementwiseComparisonOperators<float2, float2, bool2>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float2 vector-vector");

        const float scalar = 2.0f;
        AssertElementwiseComparisonOperators<float2, float, bool2>(
            left, scalar, leftVals, new double[] { scalar, scalar },
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float2 vector-scalar");
        AssertElementwiseComparisonOperators<float, float2, bool2>(
            scalar, left, new double[] { scalar, scalar }, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float2 scalar-vector");

        // A value compared against an exact copy of itself: every component ties. This is the
        // complementary fixture to the no-tie one above. A tie is where < and <= (or > and >=)
        // actually disagree, so it catches a strict/non-strict boundary swap (e.g. < mutated to
        // <=) that a fixture with no equal components cannot: unlike a threshold compared against
        // an arbitrary float, an exact self-tie is neither rare nor equivalent here, since these
        // are general-purpose comparison operators (bounds/containment checks compare against
        // literal endpoints routinely, as CultMath's own touching-edges rect test does).
        AssertElementwiseComparisonOperators<float2, float2, bool2>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float2 tied vector-vector");

        // The scalar overloads need their own tie: a uniform vector against a scalar equal to
        // every component, so every component of the vector-scalar and scalar-vector overloads
        // ties too (the vector-vector tie above does not cover them).
        var uniform = new float2(4.0f, 4.0f);
        var uniformVals = new double[] { 4.0, 4.0 };
        AssertElementwiseComparisonOperators<float2, float, bool2>(
            uniform, 4.0f, uniformVals, uniformVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float2 tied vector-scalar");
        AssertElementwiseComparisonOperators<float, float2, bool2>(
            4.0f, uniform, uniformVals, uniformVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float2 tied scalar-vector");
    }

    // ---- float3 -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Float3EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new float3(3.0f, -7.0f, 4.5f);
        var b = a;
        b[index] += 5.0f;

        AssertEqualsAndHashCodeContract(a, a, true, "float3 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"float3 differing at {index}");
        AssertElementwiseEqualityOperators<float3, float3, bool3>(
            a, b, 3, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float3 vector==vector");

        var scalar = a[index];
        AssertElementwiseEqualityOperators<float3, float, bool3>(
            a, scalar, 3, i => a[i] == scalar, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float3 vector==scalar");
        AssertElementwiseEqualityOperators<float, float3, bool3>(
            scalar, a, 3, i => a[i] == scalar, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float3 scalar==vector");
    }

    [Fact]
    public void Float3ComparisonOperatorsDifferPerComponent()
    {
        // No component is tied: a tie makes strict < and strict > agree (both false), which
        // would hide a direction-flip mutation (< swapped for >) at that component.
        var left = new float3(1.0f, 5.0f, 7.0f);
        var right = new float3(2.0f, 3.0f, 4.0f);
        var leftVals = new double[] { 1.0, 5.0, 7.0 };
        var rightVals = new double[] { 2.0, 3.0, 4.0 };

        AssertElementwiseComparisonOperators<float3, float3, bool3>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float3 vector-vector");

        const float scalar = 4.0f;
        AssertElementwiseComparisonOperators<float3, float, bool3>(
            left, scalar, leftVals, new double[] { scalar, scalar, scalar },
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float3 vector-scalar");
        AssertElementwiseComparisonOperators<float, float3, bool3>(
            scalar, left, new double[] { scalar, scalar, scalar }, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float3 scalar-vector");

        // Tied against itself (see the float2 tied-fixture comment above).
        AssertElementwiseComparisonOperators<float3, float3, bool3>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float3 tied vector-vector");

        // The scalar overloads need their own tie (see the float2 uniform-vector comment above).
        var uniform = new float3(4.0f, 4.0f, 4.0f);
        var uniformVals = new double[] { 4.0, 4.0, 4.0 };
        AssertElementwiseComparisonOperators<float3, float, bool3>(
            uniform, 4.0f, uniformVals, uniformVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float3 tied vector-scalar");
        AssertElementwiseComparisonOperators<float, float3, bool3>(
            4.0f, uniform, uniformVals, uniformVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float3 tied scalar-vector");
    }

    // ---- float4 -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Float4EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new float4(3.0f, -7.0f, 4.5f, 9.0f);
        var b = a;
        b[index] += 5.0f;

        AssertEqualsAndHashCodeContract(a, a, true, "float4 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"float4 differing at {index}");
        AssertElementwiseEqualityOperators<float4, float4, bool4>(
            a, b, 4, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float4 vector==vector");

        var scalar = a[index];
        AssertElementwiseEqualityOperators<float4, float, bool4>(
            a, scalar, 4, i => a[i] == scalar, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float4 vector==scalar");
        AssertElementwiseEqualityOperators<float, float4, bool4>(
            scalar, a, 4, i => a[i] == scalar, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "float4 scalar==vector");
    }

    [Fact]
    public void Float4ComparisonOperatorsDifferPerComponent()
    {
        // No component is tied (see the float3 comment above for why a tie hides a
        // direction-flip mutation at that component).
        var left = new float4(1.0f, 5.0f, 7.0f, 2.0f);
        var right = new float4(2.0f, 3.0f, 4.0f, 9.0f);
        var leftVals = new double[] { 1.0, 5.0, 7.0, 2.0 };
        var rightVals = new double[] { 2.0, 3.0, 4.0, 9.0 };

        AssertElementwiseComparisonOperators<float4, float4, bool4>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float4 vector-vector");

        const float scalar = 4.0f;
        AssertElementwiseComparisonOperators<float4, float, bool4>(
            left, scalar, leftVals, new double[] { scalar, scalar, scalar, scalar },
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float4 vector-scalar");
        AssertElementwiseComparisonOperators<float, float4, bool4>(
            scalar, left, new double[] { scalar, scalar, scalar, scalar }, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float4 scalar-vector");

        // Tied against itself (see the float2 tied-fixture comment above).
        AssertElementwiseComparisonOperators<float4, float4, bool4>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float4 tied vector-vector");

        // The scalar overloads need their own tie (see the float2 uniform-vector comment above).
        var uniform = new float4(4.0f, 4.0f, 4.0f, 4.0f);
        var uniformVals = new double[] { 4.0, 4.0, 4.0, 4.0 };
        AssertElementwiseComparisonOperators<float4, float, bool4>(
            uniform, 4.0f, uniformVals, uniformVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float4 tied vector-scalar");
        AssertElementwiseComparisonOperators<float, float4, bool4>(
            4.0f, uniform, uniformVals, uniformVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "float4 tied scalar-vector");
    }

    // ---- double2 ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Double2EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new double2(3.0, -7.0);
        var b = a;
        b[index] += 5.0;

        AssertEqualsAndHashCodeContract(a, a, true, "double2 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"double2 differing at {index}");
        AssertElementwiseEqualityOperators<double2, double2, bool2>(
            a, b, 2, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "double2 ==");
    }

    [Fact]
    public void Double2ComparisonOperatorsDifferPerComponent()
    {
        var left = new double2(1.0, 5.0);
        var right = new double2(2.0, 3.0);
        var leftVals = new double[] { 1.0, 5.0 };
        var rightVals = new double[] { 2.0, 3.0 };

        AssertElementwiseComparisonOperators<double2, double2, bool2>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "double2 vector-vector");

        // Tied against itself (see the float2 tied-fixture comment above).
        AssertElementwiseComparisonOperators<double2, double2, bool2>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "double2 tied vector-vector");
    }

    // ---- double3 ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Double3EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new double3(3.0, -7.0, 4.5);
        var b = a;
        b[index] += 5.0;

        AssertEqualsAndHashCodeContract(a, a, true, "double3 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"double3 differing at {index}");
        AssertElementwiseEqualityOperators<double3, double3, bool3>(
            a, b, 3, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "double3 ==");
    }

    [Fact]
    public void Double3ComparisonOperatorsDifferPerComponent()
    {
        // No component is tied (see the float3 comment above).
        var left = new double3(1.0, 5.0, 7.0);
        var right = new double3(2.0, 3.0, 4.0);
        var leftVals = new double[] { 1.0, 5.0, 7.0 };
        var rightVals = new double[] { 2.0, 3.0, 4.0 };

        AssertElementwiseComparisonOperators<double3, double3, bool3>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "double3 vector-vector");

        // Tied against itself (see the float2 tied-fixture comment above).
        AssertElementwiseComparisonOperators<double3, double3, bool3>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "double3 tied vector-vector");
    }

    // ---- int2 -------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Int2EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new int2(3, -7);
        var b = a;
        b[index] += 5;

        AssertEqualsAndHashCodeContract(a, a, true, "int2 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"int2 differing at {index}");
        AssertElementwiseEqualityOperators<int2, int2, bool2>(
            a, b, 2, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "int2 ==");
    }

    [Fact]
    public void Int2ComparisonOperatorsDifferPerComponent()
    {
        var left = new int2(1, 5);
        var right = new int2(2, 3);
        var leftVals = new double[] { 1, 5 };
        var rightVals = new double[] { 2, 3 };

        AssertElementwiseComparisonOperators<int2, int2, bool2>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "int2 vector-vector");

        // Tied against itself (see the float2 tied-fixture comment above); integers make an
        // exact tie the routine case, not the rare one.
        AssertElementwiseComparisonOperators<int2, int2, bool2>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "int2 tied vector-vector");
    }

    // ---- int3 -------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Int3EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new int3(3, -7, 4);
        var b = a;
        b[index] += 5;

        AssertEqualsAndHashCodeContract(a, a, true, "int3 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"int3 differing at {index}");
        AssertElementwiseEqualityOperators<int3, int3, bool3>(
            a, b, 3, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "int3 ==");
    }

    [Fact]
    public void Int3ComparisonOperatorsDifferPerComponent()
    {
        // No component is tied (see the float3 comment above).
        var left = new int3(1, 5, 7);
        var right = new int3(2, 3, 4);
        var leftVals = new double[] { 1, 5, 7 };
        var rightVals = new double[] { 2, 3, 4 };

        AssertElementwiseComparisonOperators<int3, int3, bool3>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "int3 vector-vector");

        // Tied against itself (see the int2 tied-fixture comment above).
        AssertElementwiseComparisonOperators<int3, int3, bool3>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "int3 tied vector-vector");
    }

    // ---- int4 -------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Int4EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new int4(3, -7, 4, 9);
        var b = a;
        b[index] += 5;

        AssertEqualsAndHashCodeContract(a, a, true, "int4 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"int4 differing at {index}");
        AssertElementwiseEqualityOperators<int4, int4, bool4>(
            a, b, 4, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "int4 ==");
    }

    [Fact]
    public void Int4ComparisonOperatorsDifferPerComponent()
    {
        // No component is tied (see the float3 comment above).
        var left = new int4(1, 5, 7, 2);
        var right = new int4(2, 3, 4, 9);
        var leftVals = new double[] { 1, 5, 7, 2 };
        var rightVals = new double[] { 2, 3, 4, 9 };

        AssertElementwiseComparisonOperators<int4, int4, bool4>(
            left, right, leftVals, rightVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "int4 vector-vector");

        // Tied against itself (see the int2 tied-fixture comment above).
        AssertElementwiseComparisonOperators<int4, int4, bool4>(
            left, left, leftVals, leftVals,
            (x, y) => x < y, (x, y) => x > y, (x, y) => x <= y, (x, y) => x >= y, (v, i) => v[i], "int4 tied vector-vector");
    }

    // ---- bool2/3/4 (no ordering operators) ---------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Bool2EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new bool2(true, false);
        var b = a;
        b[index] = !b[index];

        AssertEqualsAndHashCodeContract(a, a, true, "bool2 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"bool2 differing at {index}");
        AssertElementwiseEqualityOperators<bool2, bool2, bool2>(
            a, b, 2, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "bool2 ==");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Bool3EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new bool3(true, false, true);
        var b = a;
        b[index] = !b[index];

        AssertEqualsAndHashCodeContract(a, a, true, "bool3 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"bool3 differing at {index}");
        AssertElementwiseEqualityOperators<bool3, bool3, bool3>(
            a, b, 3, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "bool3 ==");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Bool4EqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new bool4(true, false, true, false);
        var b = a;
        b[index] = !b[index];

        AssertEqualsAndHashCodeContract(a, a, true, "bool4 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"bool4 differing at {index}");
        AssertElementwiseEqualityOperators<bool4, bool4, bool4>(
            a, b, 4, i => i != index, (x, y) => x == y, (x, y) => x != y, (v, i) => v[i], "bool4 ==");
    }

    [Fact]
    public void BoolVectorFalseAndTrueConstantsHaveEveryComponentSet()
    {
        // bool2.@false/@true and bool4.@false/bool3.@true have no other consumer in this suite
        // (bool3.@false and bool4.@true are read by HlslSemanticsTests), so a wrong component in
        // their field initializers would otherwise go unnoticed.
        Assert.Equal(new bool2(false, false), bool2.@false);
        Assert.Equal(new bool2(true, true), bool2.@true);
        Assert.Equal(new bool3(false, false, false), bool3.@false);
        Assert.Equal(new bool3(true, true, true), bool3.@true);
        Assert.Equal(new bool4(false, false, false, false), bool4.@false);
        Assert.Equal(new bool4(true, true, true, true), bool4.@true);
    }

    // ---- quaternion (== and != return a plain bool, not a component vector) -----------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void QuaternionEqualsHashCodeAndOperatorsDetectSingleComponentDifference(int index)
    {
        var a = new quaternion(1.0f, 2.0f, 3.0f, 4.0f);
        float4 asFloat4 = a;
        asFloat4[index] += 5.0f;
        quaternion b = asFloat4;

        AssertEqualsAndHashCodeContract(a, a, true, "quaternion self");
        AssertEqualsAndHashCodeContract(a, b, false, $"quaternion differing at {index}");

        // quaternion's == / != are scalar bool, defined in terms of Equals: confirm they
        // actually delegate rather than reimplementing (and weakening) the comparison.
        var aCopy = new quaternion(a.x, a.y, a.z, a.w);
        Assert.Equal(a.Equals(aCopy), a == aCopy);
        Assert.Equal(!a.Equals(aCopy), a != aCopy);
        Assert.Equal(a.Equals(b), a == b);
        Assert.Equal(!a.Equals(b), a != b);
    }

    // ---- float2x2 / float3x3 (Equals/GetHashCode only, row granularity) ---------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Float2x2EqualsAndHashCodeDetectSingleRowDifference(int row)
    {
        var row0 = new float2(1.0f, 2.0f);
        var row1 = new float2(3.0f, 4.0f);
        var a = new float2x2(row0, row1);

        var differingRow0 = row == 0 ? row0 + new float2(10.0f, 0.0f) : row0;
        var differingRow1 = row == 1 ? row1 + new float2(0.0f, 10.0f) : row1;
        var b = new float2x2(differingRow0, differingRow1);

        AssertEqualsAndHashCodeContract(a, a, true, "float2x2 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"float2x2 differing at row {row}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Float3x3EqualsAndHashCodeDetectSingleRowDifference(int row)
    {
        var row0 = new float3(1.0f, 2.0f, 3.0f);
        var row1 = new float3(4.0f, 5.0f, 6.0f);
        var row2 = new float3(7.0f, 8.0f, 9.0f);
        var a = new float3x3(row0, row1, row2);

        var differingRow0 = row == 0 ? row0 + new float3(10.0f, 0.0f, 0.0f) : row0;
        var differingRow1 = row == 1 ? row1 + new float3(0.0f, 10.0f, 0.0f) : row1;
        var differingRow2 = row == 2 ? row2 + new float3(0.0f, 0.0f, 10.0f) : row2;
        var b = new float3x3(differingRow0, differingRow1, differingRow2);

        AssertEqualsAndHashCodeContract(a, a, true, "float3x3 self");
        AssertEqualsAndHashCodeContract(a, b, false, $"float3x3 differing at row {row}");
    }
}
