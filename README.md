# Dynamic Scroll View

Unity's Scroll View ( AKA `Scroll Rect`) with a large amount of elements has a performance problem, because all elements need to be instantiated as a child of the object in order to work, the Dynamic Scroll View removes this limitation by instantiating only the visible elements.