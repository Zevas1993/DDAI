extends Node

var World = null
var Editor = null
var WorldUI = null
var Root = ""

# The bridge reads Global.ModMapData directly when resolving map identity.
# The real host declares it; stubs must too.
var ModMapData = {}
