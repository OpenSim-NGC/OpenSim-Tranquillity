from xml.dom.minidom import parse, parseString
import string

dom = parse('funcs.xml')

strings = dom.childNodes[0].childNodes

for node in strings:
	for tnode in node.childNodes:
		if tnode.nodeType == tnode.TEXT_NODE:
			print string.strip(string.split(str(tnode.data), "\n")[1])

