using PursovStudio;

try
{
    foreach (string test in StrategyParser.SelfTest(args.Length == 0 ? Path.GetFullPath("ZAPRET") : args[0]))
        Console.WriteLine("PASS " + test);
    foreach (string test in ElevatedWorker.SelfTest()) Console.WriteLine("PASS " + test);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
