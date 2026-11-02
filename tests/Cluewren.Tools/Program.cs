using Cluewren.Sandbox;
using Cluewren.Tools;

return args.FirstOrDefault() switch
{
    "hold" => await Hold.Run(args[1]),
    _ => 1,
};
