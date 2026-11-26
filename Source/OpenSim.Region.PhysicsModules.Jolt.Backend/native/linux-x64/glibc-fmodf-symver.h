/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/* Forced in with -include. Pins fmodf to the glibc 2.2.5 symbol so this file
 * loads on the same glibc floor as the JoltPhysics.Native package library.
 * One inclusion per translation unit; the guard keeps a second include quiet.
 */
#ifndef JOLTC_GLIBC_FMODF_SYMVER_H
#define JOLTC_GLIBC_FMODF_SYMVER_H
__asm__(".symver fmodf,fmodf@GLIBC_2.2.5");
#endif
