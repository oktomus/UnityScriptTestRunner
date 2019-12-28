using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// Parse tests available in the current assemblies and run them.
/// </summary>
public static class EditorTestRunner
{
    /// <summary>
    /// Assemblies to ignore.
    /// </summary>
    public static List<string> IgnoredTargetAssemblies { get; set; } = new List<string>() { "UnityEngine", "mscorlib", "System" };

    /// <summary>
    /// Containg informations about failures.
    /// </summary>
    public class Report
    {
        /// <summary>
        /// True if more than one test failed or if registering tests failed.
        /// </summary>
        public bool Failed;

        /// <summary>
        /// Number of test runned.
        /// </summary>
        public int TestCount;

        /// <summary>
        /// Number of failed tests.
        /// </summary>
        public int FailedTestCount;

        /// <summary>
        /// Number of ignored tests (playmode tests).
        /// </summary>
        public int IgnoredTestCount;
    }

    public static Report TestsReport { get; private set; }

    /// <summary>
    /// A test reference.
    /// </summary>
    private class EditorTest
    {
        public readonly string Key;
        public readonly Action Test;
        public readonly List<Action> Setups;
        public readonly List<Action> Teardowns;

        public EditorTest(string title, Action test, List<Action> setups, List<Action> teardowns)
        {
            Key = title;
            Test = test;
            Setups = setups;
            Teardowns = teardowns;
        }
    }

    /// <summary>
    /// All tests that were found.
    /// </summary>
    private static Dictionary<string, List<EditorTest>> RegisteredTests { get; } = new Dictionary<string, List<EditorTest>>();

    static EditorTestRunner()
    {
        // Be sure logs are visible in the console.
        if (Application.isBatchMode)
        {
            UnityEngine.Application.logMessageReceived += (msg, trace, type) =>
            {
                if (type == LogType.Error)
                    Console.Error.Write($"[{type}] {msg}");
                else
                    Console.Write($"[{type}] {msg}");
            };
        }
    }

    #region Running

    public static void RunAllTests()
    {
        TestsReport = new Report();

        var totalStopWatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var registerStopWatch = System.Diagnostics.Stopwatch.StartNew();
            RegisterTests();
            registerStopWatch.Stop();
            Log($"Registered {RegisteredTests.Count} test classes in {registerStopWatch.Elapsed.TotalSeconds.ToString("0.00")} s\n");
        }
        catch (Exception e)
        {
            Log("Failed to register tests.", LogType.Error);
            Log(e.ToString(), LogType.Exception);
            TestsReport.Failed = true;
            return;
        }

        RunTests();

        TestsReport.Failed = TestsReport.FailedTestCount > 0;

        totalStopWatch.Stop();

        Log($"Tets total execution time: {totalStopWatch.Elapsed.TotalSeconds.ToString("0.00")} s\n");

        LogReport();
    }

    /// <summary>
    /// Run all tests currently registered.
    /// </summary>
    private static void RunTests()
    {
        bool allTestGreen = true;

        foreach (KeyValuePair<string, List<EditorTest>> entry in RegisteredTests)
        {
            int classErrorCount = 0;
            var classStopwatch = System.Diagnostics.Stopwatch.StartNew();

            Log($"[{entry.Key}] executing {entry.Value.Count} test(s)...");

            // Run all tests in this class.
            foreach (var test in entry.Value)
            {
                TestsReport.TestCount += 1;

                bool failed = false;

                try
                {
                    failed = RunTest(test);
                } 
                catch (Exception e)
                {
                    Log($"- {test.Key} Failed.", LogType.Error);
                    failed = true;
                }

                if (failed)
                {
                    TestsReport.FailedTestCount += 1;
                    classErrorCount += 1;
                }

                allTestGreen = allTestGreen && !failed;
            }

            classStopwatch.Stop();

            // Log class result.
            if (classErrorCount == 0)
            {
                Log($"[{entry.Key}] executed in {classStopwatch.Elapsed.TotalSeconds.ToString("0.00")} s\n");
            }
            else
            {
                Log($"[{entry.Key}] {classErrorCount} failure(s), executed in {classStopwatch.Elapsed.TotalSeconds.ToString("0.00")} s\n", LogType.Error);
            }
        }

        if (allTestGreen)
        {
            Log("Test Complete Successfully.\n");
        }
        else
        {
            Log("Test Failed, please see [FAILED] log.\n", LogType.Error);
        }
    }

    private static bool RunTest(EditorTest test)
    {
        var failed = false;

        try
        {
            // Setup.
            foreach (var setup in test.Setups)
            {
                setup();
            }

            // Cleanup.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var methodStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Exception exception = null;

            // Run the test.
            try
            {
                test.Test.Invoke();
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            methodStopwatch.Stop();

            if (exception == null)
            {
                Log($"- {test.Key} executed in {methodStopwatch.Elapsed.TotalSeconds.ToString("0.00")} s");
            }
            else
            {
                Log($"- {test.Key} Failed.", LogType.Error);
                failed = true;
            }
        }
        finally
        {
            foreach (var teardown in test.Teardowns)
            {
                teardown();
            }
        }

        return failed;
    }

    #endregion

    #region Regesitering, Finding

    /// <summary>
    /// Find all tests in the current assemblies.
    /// </summary>
    private static void RegisterTests()
    {
        RegisteredTests.Clear();

        foreach (var testClassType in GetTestClasses())
        {
            var test = Activator.CreateInstance(testClassType);
            var methods = testClassType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

            //=> Create actions for all setups and teardowns.
            List<Action> setups = new List<Action>();
            List<Action> teardowns = new List<Action>();

            foreach (var method in methods)
            {
                // Create a delegate for each setup attribute.
                var setup = method.GetCustomAttribute<SetUpAttribute>(true);
                if (setup != null)
                {
                    setups.Add((Action)Delegate.CreateDelegate(typeof(Action), test, method));
                }

                // Create a delegate for each teardown attribute.
                var teardown = method.GetCustomAttribute<TearDownAttribute>(true);
                if (teardown != null)
                {
                    teardowns.Add((Action)Delegate.CreateDelegate(typeof(Action), test, method));
                }
            }

            //=> Create actions for all tests.
            foreach (var method in methods)
            {
                var standardTest = method.GetCustomAttribute<NUnit.Framework.TestAttribute>(true);

                // This is not a test.
                if (standardTest is null)
                {
                    // This is not a supported test.
                    if (method.GetCustomAttribute<UnityTestAttribute>(true) != null)
                    {
                        Log($"[{testClassType.Name}] Ignoring test case {method.Name}. Playmode tests are not supported.", LogType.Warning);
                        TestsReport.IgnoredTestCount += 1;
                    }

                    continue;
                }

                // The test will be ran only once.
                if (method.GetParameters().Length == 0 && method.ReturnType == typeof(void))
                {
                    var invoke = (Action)Delegate.CreateDelegate(typeof(Action), test, method);
                    RegisterTest(invoke.Target.GetType().Name, invoke.Method.Name, invoke, setups, teardowns);
                }
                // The test will be ran multiple times with different data sets.
                else
                {
                    var testDatas = GetTestData(method);

                    if (testDatas.Count == 0)
                        throw new Exception($"{testClassType.Name}.{method.Name} not supported (multiple parameter without TestCase or return type is invalid).");

                    foreach (var dataSet in testDatas)
                    {
                        Action invoke = null;

                        if (method.IsGenericMethod)
                        {
                            var method2 = InferGenericType(method, dataSet);
                            invoke = () => method2.Invoke(test, dataSet);
                        }
                        else
                        {
                            invoke = () => method.Invoke(test, dataSet);
                        }

                        var name = $"{method.Name}({string.Join(", ", dataSet.Select(x => x?.ToString() ?? "null"))})";
                        name = name.Replace(Char.MinValue, ' ').Replace(Char.MaxValue, ' ').Replace("<", "[").Replace(">", "]");
                        RegisterTest(test.GetType().Name, name, invoke, setups, teardowns);
                    }
                }
            }
        }
    }

    private static void RegisterTest(string group, string title, Action test, List<Action> setups, List<Action> teardowns)
    {
        if (!RegisteredTests.ContainsKey(group))
        {
            RegisteredTests[group] = new List<EditorTest>();
        }

        RegisteredTests[group].Add(new EditorTest(title, test, setups, teardowns));
    }

    /// <summary>
    /// See https://github.com/nunit/docs/wiki/TestCase-Attribute
    /// </summary>
    private static List<object[]> GetTestData(MethodInfo methodInfo)
    {
        List<object[]> testCases = new List<object[]>();

        var inlineData = methodInfo.GetCustomAttributes<NUnit.Framework.TestCaseAttribute>(true);
        foreach (var item in inlineData)
        {
            testCases.Add(item.Arguments);
        }

        foreach (var sourceAttribute in methodInfo.GetCustomAttributes<NUnit.Framework.TestCaseSourceAttribute>(true))
        {
            foreach (var sourceData in GetTestCaseSource(methodInfo, sourceAttribute.SourceType, sourceAttribute.SourceName, sourceAttribute.MethodParams))
            {
                if (sourceData is IEnumerable dataList)
                {
                    testCases.Add(dataList.OfType<object>().ToArray());
                }
            }
        }

        return testCases;
    }

    /// <summary>
    /// See https://github.com/nunit/docs/wiki/TestCaseSource-Attribute.
    /// </summary>
    private static IEnumerable GetTestCaseSource(MethodInfo method, Type sourceType, string sourceName, object[] methodParams)
    {
        Type type = sourceType ?? method.DeclaringType;

        MemberInfo[] member = type.GetMember(sourceName, BindingFlags.FlattenHierarchy | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        if (member.Length == 1)
        {
            MemberInfo memberInfo = member[0];

            if (memberInfo is FieldInfo fieldInfo)
            {
                if (!fieldInfo.IsStatic) throw new Exception("The sourceName specified on a TestCaseSourceAttribute must refer to a static field, property or method.");
                if (methodParams != null) throw new Exception("You have specified a data source field but also given a set of parameters. Fields cannot take parameters, please revise the 3rd parameter passed to the TestCaseSourceAttribute and either remove it or specify a method.");
                return ((IEnumerable)fieldInfo.GetValue(null));
            }
            else if (memberInfo is PropertyInfo propertyInfo)
            {
                if (!propertyInfo.GetGetMethod(nonPublic: true).IsStatic) throw new Exception("The sourceName specified on a TestCaseSourceAttribute must refer to a static field, property or method.");
                if (methodParams != null) throw new Exception("You have specified a data source field but also given a set of parameters. Fields cannot take parameters, please revise the 3rd parameter passed to the TestCaseSourceAttribute and either remove it or specify a method.");
                return ((IEnumerable)propertyInfo.GetValue(null, null));
            }
            else if (memberInfo is MethodInfo methodInfo)
            {
                if (!methodInfo.IsStatic) throw new Exception("The sourceName specified on a TestCaseSourceAttribute must refer to a static field, property or method.");
                if (methodParams != null && methodInfo.GetParameters().Length != methodParams.Length) throw new Exception("You have given the wrong number of arguments to the method in the TestCaseSourceAttribute, please check the number of parameters passed in the object is correct in the 3rd parameter for the TestCaseSourceAttribute and this matches the number of parameters in the target method and try again.");
                return ((IEnumerable)methodInfo.Invoke(null, methodParams));
            }
        }

        return null;
    }

    private static MethodInfo InferGenericType(MethodInfo methodInfo, object[] parameters)
    {
        var set = new HashSet<Type>();

        List<Type> genericParameters = new List<Type>();

        foreach (var item in methodInfo.GetParameters()
            .Where(x => x.ParameterType.IsGenericParameter)
            .Select((x, i) => new { x.ParameterType, i })
            .OrderBy(x => x.ParameterType.GenericParameterPosition))
        {
            if (set.Add(item.ParameterType))
            {
                genericParameters.Add(parameters[item.i].GetType());
            }
        }

        return methodInfo.MakeGenericMethod(genericParameters.ToArray());
    }

    private static IEnumerable<Type> GetTestClasses()
    {
        foreach (var assembly in GetFilteredAssemblies())
        {
            foreach (var item in assembly.GetTypes())
            {
                foreach (var method in item.GetMethods())
                {
                    // A class is a test class if it contains at least one test.
                    if (method.GetCustomAttribute<TestAttribute>(true) != null)
                    {
                        yield return item;
                        break;
                    }

                    // A class is a test class if it contains at least one playmode test (warn: we ignore playmode test).
                    if (method.GetCustomAttribute<UnityTestAttribute>(true) != null)
                    {
                        yield return item;
                        break;
                    }
                }
            }
        }
    }

    private static IEnumerable<Assembly> GetFilteredAssemblies()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = assembly.FullName;

            if (IgnoredTargetAssemblies.Any(assemblyName => n.StartsWith(assemblyName)))
                continue;

            yield return assembly;
        }
    }

    #endregion

    #region Logging

    private static void LogReport()
    {
        var type = TestsReport.Failed ? LogType.Error : LogType.Log;
        var msg = $"[{TestsReport.TestCount - TestsReport.FailedTestCount}/{TestsReport.TestCount}] test(s) succeeded.";

        Log(msg, type);

        if (TestsReport.IgnoredTestCount > 0)
        {
            Log($"{TestsReport.IgnoredTestCount} test(s) ignored.", LogType.Warning);
        }
    }

    private static void Log(string msg, LogType type = LogType.Log)
    {
        if (Application.isBatchMode)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
                Console.Error.Write(msg);
            else if (type == LogType.Warning)
                Console.WriteLine(msg);
            else
                Console.WriteLine(msg);
        }
        else
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                Debug.LogError(msg);
            else if (type == LogType.Warning)
                Debug.LogWarning(msg);
            else
                Debug.Log(msg);
        }
    }

    #endregion
}
