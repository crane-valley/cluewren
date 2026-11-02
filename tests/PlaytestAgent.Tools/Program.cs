using PlaytestAgent.Sandbox;
using PlaytestAgent.Tools;

return args.FirstOrDefault() switch
{
    "hold" => await Hold.Run(args[1]),
    _ => 1,
};
