using MediatR;
using Microsoft.EntityFrameworkCore;
using OpenSim.Data.Model.Core;


namespace OpenSim.Server.AssetServer.Events.AssetDb
{
    public class AssetExists {
        public class Request : IRequest<bool> 
        { 
            public string Id { get; set; }
        }

        public class Command : IRequestHandler<Request, bool>
        {
            private readonly OpenSimCoreContext _database;

            public Command(OpenSimCoreContext database)
            {
                this._database = database;
            }

            public async Task<bool> Handle(Request request, CancellationToken cancellationToken)
            {
                return await _database.Assets.AnyAsync(e => e.Id == request.Id);
            }
        }
    }
}