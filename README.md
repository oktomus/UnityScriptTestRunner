A simplified version of [Cysharp/RuntimeUnitTestToolkit](https://github.com/Cysharp/RuntimeUnitTestToolkit) which allow to **run Unity test cases from script**.

- Easy to use
- Simple
- Compatible with Unity Test Runner

## How to use it

```csharp
// Run tests from the command line for continuous integration.
public static void BuildAgent_RunTests()
{
    // The test runner will log by itself each test case and a final report.
    EditorTestRunner.RunAllTests();

    if (EditorTestRunner.TestsReport.Failed)
    {
        // Some tests failed.
        Application.Quit(1);
    }
    else if (EditorTestRunner.TestsReport.IgnoredTestCount)
    {
        // Some tests could not be used.
        Application.Quit(2);
    }
}
```

## Instalation

Place the `EditorTestRunner.cs` script in the `Assets/Editor` folder of your project.

## Known limitations

- Only work in Edit mode
- No playmode support (i.e. `[UnityTest]` using coroutines)
- No graphic support
- No platform support

## Contributing

This script contains the minimal amount of features required to run tests from script. But a lot can be improved and optimized, feel free to fork it and make a pull request.
