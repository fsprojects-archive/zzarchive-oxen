[![Build status](https://ci.appveyor.com/api/projects/status/m9ho080voc0yif7o/branch/master?svg=true)](https://ci.appveyor.com/project/albertjan/oxen-mcer1/branch/master)
[![Build Status](https://travis-ci.org/fsprojects/oxen.svg?branch=master)](https://travis-ci.org/fsprojects/oxen)

# Oxen
> like bull but functional

An implementation of the redis queue [bull](http://github.com/OptimalBits/bull) in f#.

## Why oxen

If you want easy persistant queues to communicate between processes
over networks or time. Between .NET and node. You can use oxen and
bull.

## Resources

 - There's a website: [here](https://fsprojects.github.io/oxen)
 - And the docs: [here](http://fsprojects.github.io/oxen/reference/index.html)

## Licences

[MIT](https://github.com/fsprojects/oxen/blob/master/LICENSE)

## Contributing

### Building

### prerequisites

|       | mac & linux        | windows            |
| ----- |:------------------:|:------------------:|
| mono  | :heavy_check_mark: |                    |
| redis | :heavy_check_mark: |                    |
| node  | :heavy_check_mark: | :heavy_check_mark: |

### commands

On mac and linux:
```sh
redis-server &
./build.sh
```

On windows:
```ps
.\build.cmd
```

### committing

 - Make sure you make a feature branch.
 - Make sure all tests pass after your changes.
 - Make sure your branch is rebased on the master branch
 - Do a PR on github
 - Write a little story about what you added/changed/fixed.
 - Profit!

## Maintainers

- Albert-Jan Nijburg [@albertjan](https://github.com/albertjan)
- Remko Boschker [@remkoboschker](https://github.com/remkoboschker)

The default maintainer account for projects under "fsprojects" is [@fsprojectsgit](https://github.com/fsprojectsgit) - F# Community Project Incubation Space (repo management)
