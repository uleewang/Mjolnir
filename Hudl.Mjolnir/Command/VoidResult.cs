﻿using System;

namespace Hudl.Mjolnir.Command
{
    [Obsolete("This will likely be removed in a future release.")]
    public class VoidResult
    {
        // TODO rob.hruska 10/18/2013 - Get rid of this void wrapper.
        // Alternative is to provide generic Command implementations that don't have a TResult.
    }
}