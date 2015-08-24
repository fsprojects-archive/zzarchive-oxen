namespace oxen.FaçadeTests
{
    using System;
    using System.Threading;
    using oxen;
    using System.Threading.Tasks;
    using Moq;
    using StackExchange.Redis;
    using Xunit;
    using XunitShould;

    public class Message
    {
        public string Thing { get; set; }
        public bool OtherThing { get; set; }
    }

    public class FaçadeTests
    {
        [Fact]
        public async Task ShouldBeAbleToAddAJobFromCSharpWithEase()
        {
            // Given
            // Transaction mock for adding the job to the wait list and sending a pubsub message
            var transactionMock = new Mock<ITransaction>();
            transactionMock
                .Setup(t => t.ListLeftPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(1L));
            transactionMock
                .Setup(t => t.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(1L));
            transactionMock
                .Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(true));
           
            // Database mock so you can add a job
            var databaseMock = new Mock<IDatabase>();
            databaseMock
                .Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
                .Returns(Task.Run(() => { }));
            databaseMock
                .Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), 1L, It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(1L));
            databaseMock
                .Setup(d => d.CreateTransaction(null))
                .Returns(transactionMock.Object);

            // So oxen can verify the internal subscription was successful
            var subscriberMock = new Mock<ISubscriber>();
            subscriberMock
                .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(1L));

            IOxenQueue<Message> queue = new Queue<Message>("test-queue", () => databaseMock.Object, () => subscriberMock.Object);

            // When
            var job = await queue.Add(new Message()
            {
                OtherThing = false,
                Thing = "yes"
            });

            // Then
            job.data.Thing.ShouldEqual("yes");
            job.data.OtherThing.ShouldBeFalse();
            job.jobId.ShouldEqual(1L);

            databaseMock.Verify(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()), Times.Once);
            databaseMock.Verify(db => db.StringIncrementAsync(It.IsAny<RedisKey>(), 1L, It.IsAny<CommandFlags>()), Times.Once);

            transactionMock.Verify(t => t.ListLeftPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Once);
            transactionMock.Verify(t => t.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Once);
        }

        [Fact]
        public async Task ShouldBeAbleToAddAHandlerToTheQueueFromCSharp()
        {
            // Given
            Action<RedisChannel, RedisValue> newJobHandler = (a, b) => { throw new Exception("shouldn't be called"); };

            // Transaction mock for moving the job to active and completed
            var transactionMock = new Mock<ITransaction>();

            // moveToSet
            transactionMock
                .Setup(t => t.ListRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(1L));
            transactionMock
                .Setup(t => t.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(true));
            transactionMock
                .Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(true));

            // Database so there's a job when you ask for one
            var databaseMock = new Mock<IDatabase>();
            databaseMock
                .Setup(d => d.CreateTransaction(null))
                .Returns(transactionMock.Object);
            // moveJob
            databaseMock
                .Setup(d => d.ListRightPopLeftPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult((RedisValue)"1"));
            // job.fromId
            databaseMock
                .Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(new[]
                {
                    new HashEntry("data", "{ \"thing\": \"yes\", \"otherThing\": true }"),
                    new HashEntry("opts", ""),
                    new HashEntry("progress", 1L),
                    new HashEntry("timestamp", (DateTime.Now - new DateTime(1970,1,1)).Milliseconds),
                    new HashEntry("delay", 0.0)
                }));
            // job.takeLock
            databaseMock
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(true));

            var subscriberMock = new Mock<ISubscriber>();
            // ensureSubscription
            subscriberMock
                .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(1L));
            // getNewJob channel
            subscriberMock
                .Setup(s => s.Subscribe("bull:test-queue:jobs", It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
                .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((channel, handler, flags) => newJobHandler = handler);

            // When
            var signal = new SemaphoreSlim(0, 1);

            IOxenQueue<Message> queue = new Queue<Message>("test-queue", () => databaseMock.Object, () => subscriberMock.Object);

            var called = false;
            queue.Process(async job =>
            {
                job.jobId.ShouldEqual(1L);
                job.data.OtherThing.ShouldBeTrue();
                job.data.Thing.ShouldEqual("yes");
                called = true;
            });

            newJobHandler("bull:test-queue:jobs", 1L);

            queue.OnJobCompleted += (obj0, obj1) => signal.Release();

            await signal.WaitAsync();
    
            // Then
            called.ShouldBeTrue();
        }
    }
}
