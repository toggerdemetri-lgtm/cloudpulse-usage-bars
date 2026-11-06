using UsageIsland.ProviderTests;

int passed = 0, failed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name); }
}
TencentTests.Run(Check);
HuaweiTests.Run(Check);
AwsTests.Run(Check);
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
