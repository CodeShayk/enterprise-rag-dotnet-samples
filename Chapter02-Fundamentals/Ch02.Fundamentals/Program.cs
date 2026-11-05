using System.Text;
using Ch02.Fundamentals;

Console.OutputEncoding = Encoding.UTF8;

switch (args.FirstOrDefault())
{
    case "tokens":
        TokenDemo.Run();
        break;
    case "index":
        IndexDemo.Run();
        break;
    default:
        Console.WriteLine("Usage: dotnet run --project Chapter02-Fundamentals/Ch02.Fundamentals -- tokens|index");
        break;
}
