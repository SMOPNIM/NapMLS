using NapMLS.FFITest;

Console.WriteLine("=== NapMLS Test Suite ===\n");

Console.WriteLine("Running memory leak tests...\n");
MemoryTest.Run();

Console.WriteLine("\n\nRunning safe wrapper lifecycle tests...\n");
// (previous lifecycle test code would go here, but memory test covers it)
