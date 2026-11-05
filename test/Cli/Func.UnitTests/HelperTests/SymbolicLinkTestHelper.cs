// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    internal static class SymbolicLinkTestHelper
    {
        /// <summary>
        /// Creates a symbolic link, or skips the test when this user cannot make one. Windows allows links for
        /// administrators and in developer mode only.
        /// </summary>
        public static void CreateOrSkip(string link, string target)
        {
            var created = false;
            try
            {
                File.CreateSymbolicLink(link, target);
                created = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The skip below reports it.
            }

            Skip.IfNot(created, "This user cannot create symbolic links here.");
        }
    }
}
