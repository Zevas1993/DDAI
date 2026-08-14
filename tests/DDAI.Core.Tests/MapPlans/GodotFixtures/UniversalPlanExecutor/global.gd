extends Node

var World = null
var Editor = null
var WorldUI = null
var Root = ""

# The bridge reads Global.ModMapData directly when reporting status. The real host
# declares it; stubs must too, since reaching for a missing property is the reflection
# that crashed Dungeondraft.
var ModMapData = {}
