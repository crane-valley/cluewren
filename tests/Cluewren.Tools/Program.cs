using System.Globalization;
using Cluewren.Sandbox;
using Cluewren.Tools;

return args.FirstOrDefault() switch
{
    "hold" => await Hold.Run(args[1]),
    "spin" => Spin.Run(double.Parse(args[1], CultureInfo.InvariantCulture), int.Parse(args[2], CultureInfo.InvariantCulture)),
    "materialize" => await Materialize.Run(args),
    _ => 1,
};
