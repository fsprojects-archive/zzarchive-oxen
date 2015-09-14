#### 0.0.1 - released
* Initial release

#### 0.0.2 - released
* fix dependencies
* fix links in nuspec

#### 0.0.3 - released
* make logger internal
* fix bug in getting job from queue hash
* fix bug with lock renewal
* serializes json to camelcase
* adds some docs

#### 0.0.4 - released
* fix dependencies add dependency to Newtonsoft.Json

#### 0.0.5 - released
* add constructor to Queue<'a> for convenience

#### 0.2.0 - released
* add delayed jobs
* add simple retry
* sync version with bull

#### 0.2.1 - released
* add stacktrace to errored jobs
* add better implementation of pause/resume

#### 0.3.0 - released
* add queue Empty event
* start of c# fa�ade
* removes internal blocking calls
* job stalledjobs before jobs on startup
* turn off log4net by default
* removes data returned from the handler (will return later when but wasn't implemented)

#### 0.4.0 - released
* adds `jobAwaiter` function to allow you to wait for a specific event to happen
