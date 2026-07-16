using Tanks.Game;
using Tanks.Net;
using Tanks.Sim;

Console.WriteLine("Hello, world!");

var simConfig = new SimConfig();
using var host = new NetworkHost();
host.Initialize(47777, Stdout.Instance);
var discovery = new ManualPeerDiscovery();
var simDriver = new SimDriver(simConfig, new Simulation(simConfig), Arena.CreateDefault(simConfig), []);