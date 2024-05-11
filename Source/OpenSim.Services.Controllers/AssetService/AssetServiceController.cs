/*
 * OpenSim.NGC Tranquillity 
 * Copyright (C) 2024 Utopia Skye LLC and its affiliates.
 * All rights reserved.

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.AspNetCore.Mvc;
using MediatR;

namespace OpenSim.Services.Controllers.AssetService
{
    [Route("assets/")]
    [ApiController]
    public class AssetServiceController : ControllerBase
    {
        private readonly IMediator _mediator;

        public AssetServiceController(IMediator mediator)
        {
            _mediator = mediator;
        }

        // // GET: assets/<asset-id>
        // [HttpGet("{id}")]
        // public async Task<ActionResult<AssetDto>> GetAsset(string id)
        // {
        //     var asset = await _mediator.Send(new GetAssetById.Request { Id = id });
        //     if (asset == null)
        //         return NotFound();

        //     return asset;
        // }
/*
        // PUT: assets/id
        // To protect from overposting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPut("{id}")]
        public async Task<IActionResult> PutAsset(string id, Asset asset)
        {
            if (id != asset.Id)
            {
                return BadRequest();
            }

            _context.Entry(asset).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!AssetExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }
*/
/*
        // POST: assets/AssetService
        // To protect from overposting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPost]
        public async Task<ActionResult<Asset>> PostAsset(Asset asset)
        {
            _context.Assets.Add(asset);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (AssetExists(asset.Id))
                {
                    return Conflict();
                }
                else
                {
                    throw;
                }
            }

            return CreatedAtAction("GetAsset", new { id = asset.Id }, asset);
        }
*/
/*
        // DELETE: assets/id
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAsset(string id)
        {
            var asset = await _context.Assets.FindAsync(id);
            if (asset == null)
            {
                return NotFound();
            }

            _context.Assets.Remove(asset);
            await _context.SaveChangesAsync();

            return NoContent();
        }
*/
/*
        private async Task<bool> AssetExists(string id)
        {
            return await _mediator.Send(new AssetExists.Request { Id = id });
        }
*/
    }
}
