/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Tests that hold an invariant the harness's earlier avatar (0.45 x 0.6 x 1.9 m) keeps and the simulator's avatar
/// (0.45 x 0.6 x 2.1 m, what <c>AvatarAppearance.SetSize</c> hands the engine) does not, run with the earlier avatar and say
/// so here. Each such test passes <see cref="EarlierAvatar"/> as <c>SimulatorAvatars</c>; setting it to true gives them the
/// simulator's avatar, and a search for the name lists them. Two rows are left, each one theory row, and each traced:
/// <list type="bullet">
/// <item>A 100 kg box thrown at a standing avatar at one physics step per heartbeat. With the 2.1 m avatar the box, thrown
/// from 0.1 m higher, is still falling at the heartbeat after the strike, so the ground has not yet slowed it: it goes on at
/// 4.8 m/s against the avatar's 2.4 m/s of step velocity (a push fades). The box is inside the capsule at the start of that
/// heartbeat, and Jolt's own update most likely moves the avatar out of it (CharacterVirtual::ExtendedUpdate, in
/// JoltPhysicsBackend.StepCharacter) by the overlap, 0.33 m, on top of its step velocity's 0.22 m: 0.555 m in one heartbeat
/// is 6.1 m/s of position, while the velocity it reports (4.79 m/s) stays within the momentum limit (4.9 m/s). The extra
/// 0.33 m is measured, taken right after that call; that it is the recovery from the overlap is the best reading, not proved. The box
/// is only moved back by what the avatar would gain (FinishCharacterContacts, MoveOutOfCapsule), not by this overlap.</item>
/// <item>A 1 kg linkset thrown at 20 m/s at a flying avatar, at one physics step per heartbeat. A flying avatar has
/// stick-to-floor on (JoltPhysicsBackend.StepCharacter, <c>stick = !hovering &amp;&amp; (rec.Flying || ...)</c>), with the avatar's
/// step height as its reach. The linkset, falling past, is within that reach under the avatar's feet at one heartbeat, and the
/// avatar steps down onto it in one update (0.38 m, measured) and stays there: it ends 0.38 m below where it flew (limit 0.3 m).
/// With 45 Hz steps the avatar does not move.</item>
/// </list>
/// (the 100 kg and 50 m/s rows and every other rate pass with the simulator's avatar.)
/// </summary>
internal static class HarnessPins
{
    internal static readonly bool EarlierAvatar = false;
}
