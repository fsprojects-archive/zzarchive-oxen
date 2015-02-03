var Queue = require('bull');
var Job = require('./node_modules/bull/lib/job');
var Promise = require('bluebird');

var messageQueue = new Queue("test-control-messages", 6379, "localhost");

messageQueue.process(function(job, cb) {
    var q = new Queue(job.data.queueName, 6379, "localhost");
    var times = parseInt(job.data.times);
    console.log("adding " + times + " job to queue " + job.data.queueName);
    var promisses = [];
    for (var i = 0; i < times; i++) {
        promisses.push(q.add({ value: "test" }));
    }
    Promise.all(promisses).then(function () {
        q.count().then(function (c) {
            console.log("q length: " + c);
            cb();
        });
    });
});

console.log("test-control-message-queue started");