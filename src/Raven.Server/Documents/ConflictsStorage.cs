﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Exceptions;
using Raven.Client.Documents.Replication.Messages;
using Raven.Server.Documents.Replication;
using Raven.Server.Documents.Versioning;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Voron;
using Voron.Data.Tables;
using Voron.Impl;
using Sparrow;
using Sparrow.Logging;
using Sparrow.Utils;
using Voron.Util;
using ConcurrencyException = Voron.Exceptions.ConcurrencyException;

namespace Raven.Server.Documents
{
    public unsafe class ConflictsStorage
    {
        private static readonly Slice KeySlice;
        private static readonly Slice KeyAndChangeVectorSlice;
        public static readonly Slice AllConflictedDocsEtagsSlice;
        private static readonly Slice ConflictedCollectionSlice;
        public static readonly Slice ConflictsSlice;

        public static readonly TableSchema ConflictsSchema = new TableSchema();

        private readonly DocumentDatabase _documentDatabase;
        private readonly DocumentsStorage _documentsStorage;
        private readonly Logger _logger;

        public long ConflictsCount;

        private enum ConflictsTable
        {
            LoweredKey = 0,
            Separator = 1,
            ChangeVector = 2,
            OriginalKey = 3,
            Data = 4,
            Etag = 5,
            Collection = 6,
            LastModified = 7,
        }

        static ConflictsStorage()
        {
            Slice.From(StorageEnvironment.LabelsContext, "Key", ByteStringType.Immutable, out KeySlice);
            Slice.From(StorageEnvironment.LabelsContext, "KeyAndChangeVector", ByteStringType.Immutable, out KeyAndChangeVectorSlice);
            Slice.From(StorageEnvironment.LabelsContext, "AllConflictedDocsEtags", ByteStringType.Immutable, out AllConflictedDocsEtagsSlice);
            Slice.From(StorageEnvironment.LabelsContext, "ConflictedCollection", ByteStringType.Immutable, out ConflictedCollectionSlice);
            Slice.From(StorageEnvironment.LabelsContext, "Conflicts", ByteStringType.Immutable, out ConflictsSlice);

            /*
             The structure of conflicts table starts with the following fields:
             [ Conflicted Doc Id | Separator | Change Vector | ... the rest of fields ... ]
             PK of the conflicts table will be 'Change Vector' field, because when dealing with conflicts,
              the change vectors will always be different, hence the uniqueness of the key. (inserts/updates will not overwrite)

            Additional indice is set to have composite key of 'Conflicted Doc Id' and 'Change Vector' so we will be able to iterate
            on conflicts by conflicted doc id (using 'starts with')

            We need a separator in order to delete all conflicts all "users/1" without deleting "users/11" conflics.
             */

            ConflictsSchema.DefineKey(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)ConflictsTable.ChangeVector,
                Count = 1,
                IsGlobal = false,
                Name = KeySlice
            });
            // required to get conflicts by key
            ConflictsSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)ConflictsTable.LoweredKey,
                Count = 3,
                IsGlobal = false,
                Name = KeyAndChangeVectorSlice
            });
            ConflictsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)ConflictsTable.Etag,
                IsGlobal = true,
                Name = AllConflictedDocsEtagsSlice
            });
            ConflictsSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)ConflictsTable.Collection,
                Count = 1,
                IsGlobal = true,
                Name = ConflictedCollectionSlice
            });
        }

        public ConflictsStorage(DocumentDatabase documentDatabase, Transaction tx)
        {
            _documentDatabase = documentDatabase;
            _documentsStorage = documentDatabase.DocumentsStorage;
            _logger = LoggingSource.Instance.GetLogger<ConflictsStorage>(documentDatabase.Name);

            ConflictsSchema.Create(tx, ConflictsSlice, 32);

            ConflictsCount = tx.OpenTable(ConflictsSchema, ConflictsSlice).NumberOfEntries;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ByteStringContext.InternalScope GetConflictsKeyPrefix(DocumentsOperationContext context, Slice lowerKey, out Slice prefixSlice)
        {
            return GetConflictsKeyPrefix(context, lowerKey.Content.Ptr, lowerKey.Size, out prefixSlice);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ByteStringContext.InternalScope GetConflictsKeyPrefix(DocumentsOperationContext context, byte* lowerKey, int lowerKeySize, out Slice prefixSlice)
        {
            ByteString keyMem;
            var scope = context.Allocator.Allocate(lowerKeySize + 1, out keyMem);

            Memory.Copy(keyMem.Ptr, lowerKey, lowerKeySize);
            keyMem.Ptr[lowerKeySize] = SpecialChars.RecordSeperator;

            prefixSlice = new Slice(SliceOptions.Key, keyMem);
            return scope;
        }

        public List<DocumentConflict> GetAllConflictsBySameKeyAfter(DocumentsOperationContext context, ref Slice lastKey)
        {
            var table = context.Transaction.InnerTransaction.OpenTable(ConflictsSchema, ConflictsSlice);
            var list = new List<DocumentConflict>();
            LazyStringValue firstKey = null;
            // Here we intentionally do not use prefix as we try to get the first conflict by empty slice
            foreach (var tvr in table.SeekForwardFrom(ConflictsSchema.Indexes[KeyAndChangeVectorSlice], lastKey, 0, false))
            {
                var conflict = TableValueToConflictDocument(context, ref tvr.Result.Reader);
                if (lastKey.Content.Match(conflict.LoweredKey))
                {
                    // same key as we already seen, skip it
                    break;
                }

                if (firstKey == null)
                    firstKey = conflict.LoweredKey;
                list.Add(conflict);

                if (firstKey.Equals(conflict.LoweredKey) == false)
                    break;
            }
            if (list.Count > 0)
            {
                lastKey.Release(context.Allocator);
                // we have to clone this, because it might be removed by the time we come back here
                Slice.From(context.Allocator, list[0].LoweredKey.Buffer, list[0].LoweredKey.Size, out lastKey);
            }
            return list;
        }

        public IEnumerable<ReplicationBatchItem> GetConflictsFrom(DocumentsOperationContext context, long etag)
        {
            var table = context.Transaction.InnerTransaction.OpenTable(ConflictsSchema, ConflictsSlice);
            foreach (var tvr in table.SeekForwardFrom(ConflictsSchema.FixedSizeIndexes[AllConflictedDocsEtagsSlice], etag, 0))
            {
                yield return ReplicationBatchItem.From(TableValueToConflictDocument(context, ref tvr.Reader));
            }
        }

        private static DocumentConflict TableValueToConflictDocument(DocumentsOperationContext context, ref TableValueReader tvr)
        {
            var result = new DocumentConflict
            {
                StorageId = tvr.Id,
                LoweredKey = DocumentsStorage.TableValueToString(context, (int)ConflictsTable.LoweredKey, ref tvr),
                Key = DocumentsStorage.TableValueToKey(context, (int)ConflictsTable.OriginalKey, ref tvr),
                ChangeVector = DocumentsStorage.GetChangeVectorEntriesFromTableValueReader(ref tvr, (int)ConflictsTable.ChangeVector),
                Etag = DocumentsStorage.TableValueToEtag((int)ConflictsTable.Etag, ref tvr),
                Collection = DocumentsStorage.TableValueToString(context, (int)ConflictsTable.Collection, ref tvr)
            };


            int size;
            var read = tvr.Read((int)ConflictsTable.Data, out size);
            if (size > 0)
            {
                //otherwise this is a tombstone conflict and should be treated as such
                result.Doc = new BlittableJsonReaderObject(read, size, context);
                DocumentsStorage.DebugDisposeReaderAfterTransction(context.Transaction, result.Doc);
            }

            result.LastModified = new DateTime(*(long*)tvr.Read((int)ConflictsTable.LastModified, out size));

            return result;
        }

        public void ThrowOnDocumentConflict(DocumentsOperationContext context, Slice loweredKey)
        {
            //TODO: don't forget to refactor this method
            Slice prefixSlice;
            using (GetConflictsKeyPrefix(context, loweredKey, out prefixSlice))
            {
                var conflicts = GetConflictsFor(context, prefixSlice);
                long largestEtag = 0;
                if (conflicts.Count > 0)
                {
                    var conflictRecords = new List<GetConflictsResult.Conflict>();
                    foreach (var conflict in conflicts)
                    {
                        if (largestEtag < conflict.Etag)
                            largestEtag = conflict.Etag;
                        conflictRecords.Add(new GetConflictsResult.Conflict
                        {
                            ChangeVector = conflict.ChangeVector
                        });
                    }

                    ThrowDocumentConflictException(loweredKey.ToString(), largestEtag);
                }
            }
        }

        private static void ThrowDocumentConflictException(string docId, long etag)
        {
            throw new DocumentConflictException($"Conflict detected on '{docId}', conflict must be resolved before the document will be accessible.", docId, etag);
        }

        public long GetConflictsMaxEtagFor(DocumentsOperationContext context, Slice prefixSlice)
        {
            if (ConflictsCount == 0)
                return 0;

            var conflictsTable = context.Transaction.InnerTransaction.OpenTable(ConflictsSchema, ConflictsSlice);
            long maxEtag = 0L;
            foreach (var tvr in conflictsTable.SeekForwardFrom(ConflictsSchema.Indexes[KeyAndChangeVectorSlice], prefixSlice, 0, true))
            {
                var etag = DocumentsStorage.TableValueToEtag((int)ConflictsTable.Etag, ref tvr.Result.Reader);
                if (maxEtag < etag)
                    maxEtag = etag;
            }
            return maxEtag;
        }

        public void DeleteConflictsFor(DocumentsOperationContext context, string key)
        {
            if (ConflictsCount == 0)
                return;

            Slice lowerKey;
            DocumentKeyWorker.GetLowerKeySliceAndStorageKey(context, key, out lowerKey, out var _);

            DeleteConflictsFor(context, lowerKey);
        }

        public IReadOnlyList<ChangeVectorEntry[]> DeleteConflictsFor(DocumentsOperationContext context, Slice lowerKey)
        {
            var list = new List<ChangeVectorEntry[]>();
            if (ConflictsCount == 0)
                return list;

            var conflictsTable = context.Transaction.InnerTransaction.OpenTable(ConflictsSchema, ConflictsSlice);
            Slice prefixSlice;
            using (GetConflictsKeyPrefix(context, lowerKey, out prefixSlice))
            {
                conflictsTable.DeleteForwardFrom(ConflictsSchema.Indexes[KeyAndChangeVectorSlice], prefixSlice, true, long.MaxValue, before =>
                {
                    var etag = DocumentsStorage.TableValueToEtag((int)ConflictsTable.Etag, ref before.Reader);
                    _documentsStorage.EnsureLastEtagIsPersisted(context, etag);

                    var changeVector = DocumentsStorage.GetChangeVectorEntriesFromTableValueReader(ref before.Reader, (int)ConflictsTable.ChangeVector);
                    list.Add(changeVector);
                });
            }

            // once this value has been set, we can't set it to false
            // an older transaction may be running and seeing it is false it
            // will not detect a conflict. It is an optimization only that
            // we have to do, so we'll handle it.

            //Only register the event if we actually deleted any conflicts
            var listCount = list.Count;
            if (listCount > 0)
            {
                var tx = context.Transaction.InnerTransaction.LowLevelTransaction;
                tx.AfterCommitWhenNewReadTransactionsPrevented += () =>
                {
                    Interlocked.Add(ref ConflictsCount, -listCount);
                };
            }
            return list;
        }

        public void DeleteConflictsFor(DocumentsOperationContext context, ChangeVectorEntry[] changeVector)
        {
            if (ConflictsCount == 0)
                return;

            var conflictsTable = context.Transaction.InnerTransaction.OpenTable(ConflictsSchema, ConflictsSlice);

            fixed (ChangeVectorEntry* pChangeVector = changeVector)
            {
                Slice changeVectorSlice;
                using (Slice.External(context.Allocator, (byte*)pChangeVector, sizeof(ChangeVectorEntry) * changeVector.Length, out changeVectorSlice))
                {
                    if (conflictsTable.DeleteByKey(changeVectorSlice))
                    {
                        var tx = context.Transaction.InnerTransaction.LowLevelTransaction;
                        tx.AfterCommitWhenNewReadTransactionsPrevented += () =>
                        {
                            Interlocked.Decrement(ref ConflictsCount);
                        };
                    }
                }
            }
        }

        public DocumentConflict GetConflictForChangeVector(
            DocumentsOperationContext context,
            string key,
            ChangeVectorEntry[] changeVector)
        {
            var conflictsTable = context.Transaction.InnerTransaction.OpenTable(ConflictsSchema, ConflictsSlice);

            Slice keyPtr;
            Slice lowerKey;
            DocumentKeyWorker.GetLowerKeySliceAndStorageKey(context, key, out lowerKey, out keyPtr);

            Slice prefixSlice;
            using (GetConflictsKeyPrefix(context, lowerKey, out prefixSlice))
            {
                foreach (var tvr in conflictsTable.SeekForwardFrom(ConflictsSchema.Indexes[KeyAndChangeVectorSlice], prefixSlice, 0, true))
                {
                    var currentChangeVector = DocumentsStorage.GetChangeVectorEntriesFromTableValueReader(ref tvr.Result.Reader, (int)ConflictsTable.ChangeVector);
                    if (currentChangeVector.SequenceEqual(changeVector))
                    {
                        int size;
                        var dataPtr = tvr.Result.Reader.Read((int)ConflictsTable.Data, out size);
                        var doc = (size == 0) ? null : new BlittableJsonReaderObject(dataPtr, size, context);
                        DocumentsStorage.DebugDisposeReaderAfterTransction(context.Transaction, doc);
                        return new DocumentConflict
                        {
                            ChangeVector = currentChangeVector,
                            Key = context.AllocateStringValue(key, tvr.Result.Reader.Read((int)ConflictsTable.OriginalKey, out size), size),
                            StorageId = tvr.Result.Reader.Id,
                            //size == 0 --> this is a tombstone conflict
                            Doc = doc
                        };
                    }
                }
            }
            return null;
        }

        public IReadOnlyList<DocumentConflict> GetConflictsFor(DocumentsOperationContext context, string key)
        {
            if (ConflictsCount == 0)
                return ImmutableAppendOnlyList<DocumentConflict>.Empty;

            Slice lowerKey, prefixSlice;
            using (DocumentKeyWorker.GetSliceFromKey(context, key, out lowerKey))
            using (GetConflictsKeyPrefix(context, lowerKey, out prefixSlice))
            {
                return GetConflictsFor(context, prefixSlice);
            }
        }

        public IReadOnlyList<DocumentConflict> GetConflictsFor(DocumentsOperationContext context, Slice prefixSlice)
        {
            var conflictsTable = context.Transaction.InnerTransaction.OpenTable(ConflictsSchema, ConflictsSlice);
            var items = new List<DocumentConflict>();
            foreach (var tvr in conflictsTable.SeekForwardFrom(ConflictsSchema.Indexes[KeyAndChangeVectorSlice], prefixSlice, 0, true))
            {
                var conflict = TableValueToConflictDocument(context, ref tvr.Result.Reader);
                items.Add(conflict);
            }
            return items;
        }

        public ChangeVectorEntry[] GetMergedConflictChangeVectorsAndDeleteConflicts(
           DocumentsOperationContext context,
           Slice lowerKey,
           long newEtag,
           ChangeVectorEntry[] existing = null)
        {
            if (ConflictsCount == 0)
                return MergeVectorsWithoutConflicts(newEtag, existing);

            var conflictChangeVectors = DeleteConflictsFor(context, lowerKey);
            if (conflictChangeVectors.Count == 0)
                return MergeVectorsWithoutConflicts(newEtag, existing);

            // need to merge the conflict change vectors
            var maxEtags = new Dictionary<Guid, long>
            {
                [_documentsStorage.Environment.DbId] = newEtag
            };

            foreach (var conflictChangeVector in conflictChangeVectors)
                foreach (var entry in conflictChangeVector)
                {
                    long etag;
                    if (maxEtags.TryGetValue(entry.DbId, out etag) == false ||
                        etag < entry.Etag)
                    {
                        maxEtags[entry.DbId] = entry.Etag;
                    }
                }

            var changeVector = new ChangeVectorEntry[maxEtags.Count];

            var index = 0;
            foreach (var maxEtag in maxEtags)
            {
                changeVector[index].DbId = maxEtag.Key;
                changeVector[index].Etag = maxEtag.Value;
                index++;
            }
            return changeVector;
        }

        private ChangeVectorEntry[] MergeVectorsWithoutConflicts(long newEtag, ChangeVectorEntry[] existing)
        {
            if (existing != null)
                return ReplicationUtils.UpdateChangeVectorWithNewEtag(_documentsStorage.Environment.DbId, newEtag, existing);

            return new[]
            {
                new ChangeVectorEntry
                {
                    Etag = newEtag,
                    DbId = _documentsStorage.Environment.DbId
                }
            };
        }

        public void ThrowConcurrencyExceptionOnConflict(DocumentsOperationContext context, byte* lowerKey, int lowerSize, long? expectedEtag)
        {
            long currentMaxConflictEtag;
            Slice prefixSlice;
            using (GetConflictsKeyPrefix(context, lowerKey, lowerSize, out prefixSlice))
            {
                currentMaxConflictEtag = GetConflictsMaxEtagFor(context, prefixSlice);
            }

            throw new ConcurrencyException(
                $"Tried to resolve document conflict with etag = {expectedEtag}, but the current max conflict etag is {currentMaxConflictEtag}. This means that the conflict information with which you are trying to resolve the conflict is outdated. Get conflict information and try resolving again.");
        }

        public ChangeVectorEntry[] MergeConflictChangeVectorIfNeededAndDeleteConflicts(ChangeVectorEntry[] documentChangeVector, DocumentsOperationContext context, string key, long newEtag)
        {
            ChangeVectorEntry[] mergedChangeVectorEntries = null;
            bool firstTime = true;
            foreach (var conflict in GetConflictsFor(context, key))
            {
                if (firstTime)
                {
                    mergedChangeVectorEntries = conflict.ChangeVector;
                    firstTime = false;
                    continue;
                }
                mergedChangeVectorEntries = ReplicationUtils.MergeVectors(mergedChangeVectorEntries, conflict.ChangeVector);
            }

            //We had conflicts need to delete them
            if (mergedChangeVectorEntries != null)
            {
                DeleteConflictsFor(context, key);
                if (documentChangeVector != null)
                    mergedChangeVectorEntries = ReplicationUtils.MergeVectors(mergedChangeVectorEntries, documentChangeVector);

                mergedChangeVectorEntries = ReplicationUtils.MergeVectors(mergedChangeVectorEntries, new[]
                {
                    new ChangeVectorEntry
                    {
                        DbId = _documentDatabase.DbId,
                        Etag = newEtag
                    }
                });

                return mergedChangeVectorEntries;
            }
            return documentChangeVector; // this covers the null && null case too
        }
    }
}