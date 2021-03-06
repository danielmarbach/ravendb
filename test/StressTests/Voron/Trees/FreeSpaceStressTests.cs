﻿using FastTests;
using FastTests.Voron.FixedSize;
using FastTests.Voron.Trees;
using Xunit;

namespace StressTests.Voron.Trees
{
    public class FreeSpaceStressTests : NoDisposalNeeded
    {
        [Theory]
        [InlineData(400000, 60, 2)] // originally set in the test
        [InlineDataWithRandomSeed(400000, 60)]
        [InlineDataWithRandomSeed(-1, -1)] // random 'maxPageNumber' and 'numberOfFreedPages'
        public void FreeSpaceHandlingShouldNotReturnPagesThatAreAlreadyAllocated(int maxPageNumber,
            int numberOfFreedPages, int seed)
        {
            using (var test = new FreeSpaceTest())
            {
                test.FreeSpaceHandlingShouldNotReturnPagesThatAreAlreadyAllocated(maxPageNumber, numberOfFreedPages, seed);
            }
        }
    }
}