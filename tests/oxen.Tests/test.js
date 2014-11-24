var Queue = require('bull');
var Promise = require('bluebird');

/**
  Adds a job to the queue.
  @method add
  @param data: {} Custom data to store for this job. Should be JSON serializable.
  @param opts: JobOptions Options for this job.
*/
Queue.prototype.add = function (data, opts) {
    var _this = this;
    opts = opts || {};

    // If we fail after incrementing the job id we may end having an unused
    // id, but this should not be so harmful
    return _this.client.incrAsync(this.toKey('id')).then(function (jobId) {
        return Job.create(_this, jobId, data, opts).then(function (job) {
            var key = _this.toKey('wait');
            var channel = _this.toKey("jobs");
            var multi = _this.client.multi();
            multi[(opts.lifo ? 'r' : 'l') + 'push'](key, jobId);
            multi.publish(channel, jobId);
            // if queue is LIFO use rpushAsync
            return multi.execAsync().then(function () {
                return job;
            });
        });
    });
}

var q = new Queue(process.argv[2], 6379, "localhost");
var times = parseInt(process.argv[3]);
console.log("adding " + times + " job to queue " + process.argv[2]);
var promisses = [];
for (var i = 0; i < times; i++) {
    promisses.push(q.add({ value: "test" }));
}
Promise.all(promisses).then(function (item) {
	q.count().then(function(c) {
		console.log("done: " + item);
		console.log("q length: " + c);
		process.exit(0); 
	});
});