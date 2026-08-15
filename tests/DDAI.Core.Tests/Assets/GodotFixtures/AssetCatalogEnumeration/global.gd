extends Node

var Header = null
var Editor = null

# The bridge reads Global.ModMapData directly when resolving map identity.
# The real host declares it; stubs must too.
var ModMapData = {}
