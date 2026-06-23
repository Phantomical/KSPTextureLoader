using System;
using System.Collections.Generic;
using KSP.Testing;

namespace KSPTextureLoaderTests.AssetBundle;

/// <summary>
/// Base class for the asset-bundle parsing tests. These run inside KSP's
/// <see cref="UnitTest"/> harness (a failure is signalled by throwing), but the
/// code under test is pure buffer parsing and needs no live game state.
/// </summary>
public abstract class BundleParseTestBase : UnitTest
{
    protected static void AssertTrue(string name, bool condition, string message = null)
    {
        if (!condition)
            throw new Exception($"{name}: FAIL! {message ?? "expected condition to hold"}");
    }

    protected static void AssertEqual<T>(string name, T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception($"{name}: FAIL! expected <{expected}> but got <{actual}>");
    }

    protected static void AssertBytesEqual(string name, byte[] actual, byte[] expected)
    {
        if (actual is null || expected is null)
            throw new Exception($"{name}: FAIL! null array (actual={actual}, expected={expected})");
        if (actual.Length != expected.Length)
            throw new Exception(
                $"{name}: FAIL! length {actual.Length} != expected {expected.Length}"
            );
        for (int i = 0; i < actual.Length; ++i)
            if (actual[i] != expected[i])
                throw new Exception(
                    $"{name}: FAIL! byte[{i}] = {actual[i]} != expected {expected[i]}"
                );
    }

    protected static void AssertThrows<TException>(string name, Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception e)
        {
            throw new Exception(
                $"{name}: FAIL! expected {typeof(TException).Name} but got {e.GetType().Name}: {e.Message}"
            );
        }

        throw new Exception(
            $"{name}: FAIL! expected {typeof(TException).Name} but nothing was thrown"
        );
    }
}
