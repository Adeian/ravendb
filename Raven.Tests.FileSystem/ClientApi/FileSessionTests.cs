﻿using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.FileSystem;
using Raven.Client.FileSystem;
using Raven.Json.Linq;
using Raven.Tests.Common;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Raven.Tests.FileSystem.ClientApi
{
    public class FileSessionTests : RavenFilesTestWithLogs
    {

        private readonly IFilesStore filesStore;

        public FileSessionTests()
        {
            filesStore = this.NewStore();
        }

        [Fact]
        public void SessionLifecycle()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                Assert.NotNull(session.Advanced);
                Assert.True(session.Advanced.MaxNumberOfRequestsPerSession == 30);
                Assert.False(string.IsNullOrWhiteSpace(session.Advanced.StoreIdentifier));
                Assert.Equal(filesStore, session.Advanced.FilesStore);
                Assert.Equal(filesStore.Identifier, session.Advanced.StoreIdentifier.Split(';')[0]);
                Assert.Equal(store.DefaultFileSystem, session.Advanced.StoreIdentifier.Split(';')[1]);
            }

            store.Conventions.MaxNumberOfRequestsPerSession = 10;

            using (var session = filesStore.OpenAsyncSession())
            {
                Assert.True(session.Advanced.MaxNumberOfRequestsPerSession == 10);
            }
        }

        [Fact]
        public async Task EnsureMaxNumberOfRequestsPerSessionIsHonored()
        {
            var store = (FilesStore)filesStore;
            store.Conventions.MaxNumberOfRequestsPerSession = 0;

            using (var session = filesStore.OpenAsyncSession())
            {
                await AssertAsync.Throws<InvalidOperationException>(() => session.LoadFileAsync("test1.file"));
                await AssertAsync.Throws<InvalidOperationException>(() => session.DownloadAsync("test1.file"));
                Assert.Throws<InvalidOperationException>(() => session.RegisterFileDeletion("test1.file"));
                Assert.Throws<InvalidOperationException>(() => session.RegisterRename("test1.file", "test2.file"));
                Assert.Throws<InvalidOperationException>(() => session.RegisterUpload("test1.file", CreateUniformFileStream(128)));
            }
        }

        [Fact]
		public async Task UploadWithDeferredAction()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 128; i++)
                        x.WriteByte(i);
                });

                await session.SaveChangesAsync();

                var file = await session.LoadFileAsync("test1.file");
                var resultingStream = await session.DownloadAsync(file);

                var ms = new MemoryStream();
                resultingStream.CopyTo(ms);
                ms.Seek(0, SeekOrigin.Begin);

                Assert.Equal(128, ms.Length);                

                for (byte i = 0; i < 128; i++)
                {
                    int value = ms.ReadByte();
                    Assert.True(value >= 0);
                    Assert.Equal(i, (byte)value);
                }
            }
        }

        [Fact]
		public async Task UploadActionWritesIncompleteStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 60; i++)
                        x.WriteByte(i);
                });

                await AssertAsync.Throws<BadRequestException>(() => session.SaveChangesAsync());
            }
        }

        [Fact]
		public async Task UploadActionWritesIncompleteWithErrorStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 60; i++)
                        x.WriteByte(i);
                    
                    // We are throwing to break the upload. RavenFS client should detect this case and cancel the upload. 
                    throw new Exception();
                });

                await AssertAsync.Throws<BadRequestException>(() => session.SaveChangesAsync());
            }
        }

        [Fact]
		public async Task UploadAndDeleteFileOnDifferentSessions()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test2.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();
            }

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterFileDeletion("test1.file");

                var file = await session.LoadFileAsync("test1.file");
                Assert.NotNull(file);

                await session.SaveChangesAsync();

                file = await session.LoadFileAsync("test1.file");
                Assert.Null(file);
            }
        }

        [Fact]
        public async Task RenameWithDirectoryChange()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("a/test1.file", CreateUniformFileStream(128));
                session.RegisterRename("a/test1.file", "a/a/test1.file");
                await session.SaveChangesAsync();

                var deletedFile = await session.LoadFileAsync("/a/test1.file");
                Assert.Null(deletedFile);

                var availableFile = await session.LoadFileAsync("/a/a/test1.file");
                Assert.NotNull(availableFile);
            }
        }

        [Fact]
        public async Task RenameWithoutDirectoryChange()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                session.RegisterRename("/b/test1.file", "/b/test2.file");
                await session.SaveChangesAsync();

                var deletedFile = await session.LoadFileAsync("b/test1.file");
                Assert.Null(deletedFile);

                var availableFile = await session.LoadFileAsync("b/test2.file");
                Assert.NotNull(availableFile);
            }
        }

        [Fact]
        public async Task EnsureSlashPrefixWorks()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                var fileWithoutPrefix = await session.LoadFileAsync("test1.file");
                var fileWithPrefix = await session.LoadFileAsync("/test1.file");
                Assert.NotNull(fileWithoutPrefix);
                Assert.NotNull(fileWithPrefix);

                fileWithoutPrefix = await session.LoadFileAsync("b/test1.file");
                fileWithPrefix = await session.LoadFileAsync("/b/test1.file");
                Assert.NotNull(fileWithoutPrefix);
                Assert.NotNull(fileWithPrefix);
            }
        }


        [Fact]
        public async Task EnsureTwoLoadsWillReturnSameObject()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

               var firstCallFile = await session.LoadFileAsync("test1.file");
                var secondCallFile = await session.LoadFileAsync("test1.file");
                Assert.Equal(firstCallFile, secondCallFile);

                firstCallFile = await session.LoadFileAsync("/b/test1.file");
                secondCallFile = await session.LoadFileAsync("/b/test1.file");
                Assert.Equal(firstCallFile, secondCallFile);
            }
        }


        [Fact]
        public async Task DownloadStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                var fileStream = CreateUniformFileStream(128);
                session.RegisterUpload("test1.file", fileStream);
                await session.SaveChangesAsync();

                fileStream.Seek(0, SeekOrigin.Begin);

                var file = await session.LoadFileAsync("test1.file");

                var resultingStream = await session.DownloadAsync(file);

                var originalText = new StreamReader(fileStream).ReadToEnd();
                var downloadedText = new StreamReader(resultingStream).ReadToEnd();
                Assert.Equal(originalText, downloadedText);

                //now downloading file with metadata

                Reference<RavenJObject> metadata = new Reference<RavenJObject>();
                resultingStream = await session.DownloadAsync("test1.file", metadata);

                Assert.NotNull(metadata.Value);
                Assert.Equal(128, metadata.Value.Value<long>("RavenFS-Size"));
            }
        }

        [Fact]
        public async Task SaveIsIncompleteEnsureAllPendingOperationsAreCancelledStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                var fileStream = CreateUniformFileStream(128);
                session.RegisterUpload("test2.file", fileStream);
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 60; i++)
                        x.WriteByte(i);
                });
                session.RegisterRename("test2.file", "test3.file");

                await AssertAsync.Throws<BadRequestException>(() => session.SaveChangesAsync());

                var shouldExist = await session.LoadFileAsync("test2.file");
                Assert.NotNull(shouldExist);
                var shouldNotExist = await session.LoadFileAsync("test3.file");
                Assert.Null(shouldNotExist);
            }
        }

        [Fact]
        public async Task LoadMultipleFileHeaders()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                var files = await session.LoadFileAsync(new String[] { "/b/test1.file", "test1.file" });

                Assert.NotNull(files);
            }
        }

        [Fact]
        public async Task MetadataUpdateWithRenames()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                // Modify metadata and then rename
                var file = await session.LoadFileAsync("test1.file");
                file.Metadata["Test"] = new RavenJValue("Value");
                session.RegisterRename("test1.file", "test2.file");

                await session.SaveChangesAsync();

                file = await session.LoadFileAsync("test2.file");
                
                Assert.Null(await session.LoadFileAsync("test1.file"));
                Assert.NotNull(file);
                Assert.True(file.Metadata.ContainsKey("Test"));

                // Rename and then modify metadata
                session.RegisterRename("test2.file", "test3.file");
                file.Metadata["Test2"] = new RavenJValue("Value");

                await session.SaveChangesAsync();

                file = await session.LoadFileAsync("test3.file");
                
                Assert.Null(await session.LoadFileAsync("test2.file"));
                Assert.NotNull(file);
                Assert.True(file.Metadata.ContainsKey("Test"));
                Assert.True(file.Metadata.ContainsKey("Test2"));
            }
        }

        [Fact]
        public async Task MetadataUpdateWithContentUpdate()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                // content update after a metadata change
                var file = await session.LoadFileAsync("test1.file");
                file.Metadata["Test"] = new RavenJValue("Value");
                session.RegisterUpload("test1.file", CreateUniformFileStream(180));

                await session.SaveChangesAsync();

                Assert.True(file.Metadata.ContainsKey("Test"));
                Assert.Equal(180, file.TotalSize);

                // content update using file header
                file.Metadata["Test2"] = new RavenJValue("Value");
                session.RegisterUpload(file, CreateUniformFileStream(120));

                await session.SaveChangesAsync();

                Assert.True(file.Metadata.ContainsKey("Test"));
                Assert.True(file.Metadata.ContainsKey("Test2"));
                Assert.Equal(120, file.TotalSize);
            }
        }

        [Fact]
        public async Task MetadataUpdateWithDeletes()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                // deleting file after a metadata change
                var file = await session.LoadFileAsync("test1.file");
                file.Metadata["Test"] = new RavenJValue("Value");
                session.RegisterFileDeletion("test1.file");

                await session.SaveChangesAsync();
                
                Assert.Null(await session.LoadFileAsync("test1.file"));

                // deleting file after a metadata change
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                file = await session.LoadFileAsync("test1.file");
                session.RegisterFileDeletion("test1.file");
                await session.SaveChangesAsync();

                file.Metadata["Test"] = new RavenJValue("Value");
                await session.SaveChangesAsync();

                file = await session.LoadFileAsync("test1.file");
                Assert.Null(file);
            }
        }

        [Fact]
        public async Task WorkingWithMultipleFiles()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                // Uploading 10 files
                for ( int i = 0; i < 10; i++ )
                {
                    session.RegisterUpload(string.Format("test{0}.file", i), CreateUniformFileStream(128));
                }

                await session.SaveChangesAsync();

                // Some files are then deleted and some are updated
                for (int i = 0; i < 10; i++)
                {
                    if (i % 2 == 0)
                    {
                        var file = await session.LoadFileAsync(string.Format("test{0}.file", i));
                        file.Metadata["Test"] = new RavenJValue("Value");
                    }
                    else
                    {
                        session.RegisterFileDeletion(string.Format("test{0}.file", i));
                    }
                }

                await session.SaveChangesAsync();

                // Finally we assert over all the files to see if they were treated as expected
                for (int i = 0; i < 10; i++)
                {
                    if (i % 2 == 0)
                    {
                        var file = await session.LoadFileAsync(string.Format("test{0}.file", i));
                        Assert.True(file.Metadata.ContainsKey("Test"));
                    }
                    else
                    {
                        Assert.Null(await session.LoadFileAsync(string.Format("test{0}.file", i)));
                    }
                }
            }
        }

        [Fact]
        public async Task CombinationOfDeletesAndUpdatesNotPermitted()
        {
            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                // deleting file, then uploading it again and doing metadata change
                session.RegisterFileDeletion("test1.file");
	            Assert.Throws<InvalidOperationException>(() => session.RegisterUpload("test1.file", CreateUniformFileStream(128)));
            }
        }

        [Fact]
        public async Task MultipleLoadsInTheSameCall()
        {
            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test.file", CreateUniformFileStream(10));
                session.RegisterUpload("test.fil", CreateUniformFileStream(10));
                session.RegisterUpload("test.fi", CreateUniformFileStream(10));
                session.RegisterUpload("test.f", CreateUniformFileStream(10));
                await session.SaveChangesAsync();

                var names = new string[] { "test.file", "test.fil", "test.fi", "test.f" };
                var query = await session.LoadFileAsync(names);

                Assert.False(query.Any(x => x == null));
            }
        }

        [Fact]
        public async Task MetadataDatesArePreserved()
        {
            FileHeader originalFile;
            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                // content update after a metadata change
                originalFile = await session.LoadFileAsync("test1.file");
                originalFile.Metadata["Test"] = new RavenJValue("Value");

                DateTimeOffset originalCreationDate = originalFile.CreationDate;
                var metadataCreationDate = originalFile.Metadata[Constants.RavenCreationDate];

                await session.SaveChangesAsync();

                Assert.Equal(originalCreationDate, originalFile.CreationDate);
                Assert.Equal(metadataCreationDate, originalFile.Metadata[Constants.RavenCreationDate]);
            }
        }
    }
}
