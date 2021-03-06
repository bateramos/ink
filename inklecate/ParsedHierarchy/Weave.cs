﻿using System.Collections.Generic;

namespace Ink.Parsed
{
    // Used by the FlowBase when constructing the weave flow from
    // a flat list of content objects.
    internal class Weave : Parsed.Object
    {
        // Containers can be chained as multiple gather points
        // get created as the same indentation level.
        // rootContainer is always the first in the chain, while
        // currentContainer is the latest.
        public Runtime.Container rootContainer { 
            get {
                if (_rootContainer == null) {
                    GenerateRuntimeObject ();
                }

                return _rootContainer;
            }
        }
        Runtime.Container currentContainer { get; set; }

		public int baseIndentIndex { get; private set; }

        // Loose ends are:
        //  - Choices or Gathers that need to be joined up
        //  - Explicit Divert to gather points (i.e. "->" without a target)
        public List<Parsed.Object> looseEnds;

        public List<GatherPointToResolve> gatherPointsToResolve;
        internal class GatherPointToResolve
        {
            public Runtime.Divert divert;
            public Runtime.Object targetRuntimeObj;
        }

        public Parsed.Object lastParsedObject
        {
            get {
                if (content.Count > 0) {

                    // Don't count extraneous newlines
                    Parsed.Object lastObject = null;
                    for (int i = content.Count - 1; i >= 0; --i) {
                        lastObject = content [i];
                        var lastText = lastObject as Parsed.Text;
                        if (lastText == null || lastText.text != "\n") {
                            break;
                        }
                    }

                    var lastWeave = lastObject as Weave;
                    if (lastWeave)
                        return lastWeave.lastParsedObject;
                    else
                        return lastObject;
                } else {
                    return this;
                }
            }
        }
                        
        public Weave(List<Parsed.Object> cont, int indentIndex=-1) 
        {
            if (indentIndex == -1) {
                baseIndentIndex = DetermineBaseIndentationFromContent (cont);
            } else {
                baseIndentIndex = indentIndex;
            }

            AddContent (cont);

            ConstructWeaveHierarchyFromIndentation ();

            // Only base level weaves keep track of named weave points
            if (indentIndex == 0) {

                var namedWeavePoints = FindAll<IWeavePoint> (w => w.name != null && w.name.Length > 0);

                _namedWeavePoints = new Dictionary<string, IWeavePoint> ();

                foreach (var weavePoint in namedWeavePoints) {
                    _namedWeavePoints [weavePoint.name] = weavePoint;
                }
            }
        }

        void ConstructWeaveHierarchyFromIndentation()
        {
            // Find nested indentation and convert to a proper object hierarchy
            // (i.e. indented content is replaced with a Weave object that contains
            // that nested content)
            int contentIdx = 0;
            while (contentIdx < content.Count) {

                Parsed.Object obj = content [contentIdx];

                // Choice or Gather
                if (obj is IWeavePoint) {
                    var weavePoint = (IWeavePoint)obj;
                    var weaveIndentIdx = weavePoint.indentationDepth - 1;

                    // Inner level indentation - recurse
                    if (weaveIndentIdx > baseIndentIndex) {

                        // Step through content until indent jumps out again
                        int innerWeaveStartIdx = contentIdx;
                        while (contentIdx < content.Count) {
                            var innerWeaveObj = content [contentIdx] as IWeavePoint;
                            if (innerWeaveObj != null) {
                                var innerIndentIdx = innerWeaveObj.indentationDepth - 1;
                                if (innerIndentIdx <= baseIndentIndex) {
                                    break;
                                }
                            }

                            contentIdx++;
                        }

                        int weaveContentCount = contentIdx - innerWeaveStartIdx;

                        var weaveContent = content.GetRange (innerWeaveStartIdx, weaveContentCount);
                        content.RemoveRange (innerWeaveStartIdx, weaveContentCount);

                        var weave = new Weave (weaveContent, weaveIndentIdx);
                        InsertContent (innerWeaveStartIdx, weave);

                        // Continue iteration from this point
                        contentIdx = innerWeaveStartIdx;
                    }

                } 

                contentIdx++;
            }
        }
            
        // When the indentation wasn't told to us at construction time using
        // a choice point with a known indentation level, we may be told to
        // determine the indentation level by incrementing from our closest ancestor.
        public int DetermineBaseIndentationFromContent(List<Parsed.Object> contentList)
        {
            foreach (var obj in contentList) {
                if (obj is IWeavePoint) {
                    return ((IWeavePoint)obj).indentationDepth - 1;
                }
            }

            // No weave points, so it doesn't matter
            return 0;
        }

        public override Runtime.Object GenerateRuntimeObject ()
        {
            _rootContainer = currentContainer = new Runtime.Container();
            looseEnds = new List<Parsed.Object> ();

            gatherPointsToResolve = new List<GatherPointToResolve> ();

            // Iterate through content for the block at this level of indentation
            //  - Normal content is nested under Choices and Gathers
            //  - Blocks that are further indented cause recursion
            //  - Keep track of loose ends so that they can be diverted to Gathers
            foreach(var obj in content) {

                // Choice or Gather
                if (obj is IWeavePoint) {
                    AddRuntimeForWeavePoint ((IWeavePoint)obj);
                } 

                // Non-weave point
                else {

                    // Nested weave
                    if (obj is Weave) {
                        var weave = (Weave)obj;
                        AddRuntimeForNestedWeave (weave);
                        gatherPointsToResolve.AddRange (weave.gatherPointsToResolve);
                    }

                    // Other object
                    // May be complex object that contains statements - e.g. a multi-line conditional
                    else {

                        // Find any nested explicit gather points within this object
                        // (including the object itself)
                        // i.e. instances of "->" without a target that's meant to go 
                        // to the next gather point.
                        var innerExplicitGathers = obj.FindAll<Divert> (d => d.isToGather);
                        if (innerExplicitGathers.Count > 0)
                            looseEnds.AddRange (innerExplicitGathers.ToArray());

                        // Add content
                        AddGeneralRuntimeContent (obj.runtimeObject);
                    }

                    // Keep track of nested choices within this (possibly complex) object,
                    // so that the next Gather knows whether to auto-enter
                    // (it auto-enters when there are no choices)
                    var innerChoices = obj.FindAll<Choice> ();
                    if (innerChoices.Count > 0)
                        hasSeenChoiceInSection = true;

                }
            }

            // Pass any loose ends up the hierarhcy
            PassLooseEndsToAncestors();

            return _rootContainer;
        }

        // Found gather point:
        //  - gather any loose ends
        //  - set the gather as the main container to dump new content in
        void AddRuntimeForGather(Gather gather)
        {
            // Determine whether this Gather should be auto-entered:
            //  - It is auto-entered if there were no choices in the last section
            //  - A section is "since the previous gather" - so reset now
            bool autoEnter = !hasSeenChoiceInSection;
            hasSeenChoiceInSection = false;

            var gatherContainer = gather.runtimeContainer;

            if (gather.name == null) {
                // Use disallowed character so it's impossible to have a name collision
                gatherContainer.name = "g-" + _unnamedGatherCount;
                _unnamedGatherCount++;
            }
                
            // Auto-enter: include in main content
            if (autoEnter) {
                currentContainer.AddContent (gatherContainer);
            } 

            // Don't auto-enter:
            // Add this gather to the main content, but only accessible
            // by name so that it isn't stepped into automatically, but only via
            // a divert from a loose end.
            else {
                currentContainer.AddToNamedContentOnly (gatherContainer);
            }

            // Consume loose ends: divert them to this gather
            foreach (Parsed.Object looseEnd in looseEnds) {

                // Skip gather loose ends that are at the same level
                // since they'll be handled by the auto-enter code below
                // that only jumps into the gather if (current runtime choices == 0)
                if (looseEnd is Gather) {
                    var prevGather = (Gather)looseEnd;
                    if (prevGather.indentationDepth == gather.indentationDepth) {
                        continue;
                    }
                }

                Runtime.Divert divert = null;

                if (looseEnd is Parsed.Divert) {
                    divert = (Runtime.Divert) looseEnd.runtimeObject;
                } else {
                    var looseWeavePoint = looseEnd as IWeavePoint;

                    var looseChoice = looseWeavePoint as Parsed.Choice;
                    if (looseChoice && looseChoice.hasTerminatingDivert) {
                        divert = looseChoice.terminatingDivert.runtimeObject as Runtime.Divert;
                    } else {
                        divert = new Runtime.Divert ();
                        looseWeavePoint.runtimeContainer.AddContent (divert);
                    }
                }
                   
                // Pass back knowledge of this loose end being diverted
                // to the FlowBase so that it can maintain a list of them,
                // and resolve the divert references later
                gatherPointsToResolve.Add (new GatherPointToResolve{ divert = divert, targetRuntimeObj = gatherContainer });
            }
            looseEnds.Clear ();

            // Replace the current container itself
            currentContainer = gatherContainer;
        }

        void AddRuntimeForWeavePoint(IWeavePoint weavePoint)
        {
            // Current level Gather
            if (weavePoint is Gather) {
                AddRuntimeForGather ((Gather)weavePoint);
            } 

            // Current level choice
            else if (weavePoint is Choice) {

                // Gathers that contain choices are no longer loose ends
                // (same as when weave points get nested content)
                if (previousWeavePoint is Gather) {
                    looseEnds.Remove ((Parsed.Object)previousWeavePoint);
                }

                currentContainer.AddContent (((Choice)weavePoint).runtimeObject);
                hasSeenChoiceInSection = true;
            }

            // Keep track of loose ends
            addContentToPreviousWeavePoint = false; // default
            if (WeavePointHasLooseEnd (weavePoint)) {
                looseEnds.Add ((Parsed.Object)weavePoint);

                // If choice has an explicit gather divert ("->") then it doesn't need content added to it
                var looseChoice = weavePoint as Choice;
                if (looseChoice && !looseChoice.hasExplicitGather) {
                    addContentToPreviousWeavePoint = true;
                }
            }
            previousWeavePoint = weavePoint;
        }

        // Add nested block at a greater indentation level
        public void AddRuntimeForNestedWeave(Weave nestedResult)
        {
            // Add this inner block to current container
            // (i.e. within the main container, or within the last defined Choice/Gather)
            AddGeneralRuntimeContent (nestedResult.rootContainer);

            // Now there's a deeper indentation level, the previous weave point doesn't
            // count as a loose end (since it will have content to go to)
            if (previousWeavePoint != null) {
                looseEnds.Remove ((Parsed.Object)previousWeavePoint);
                addContentToPreviousWeavePoint = false;
            }
        }

        // Normal content gets added into the latest Choice or Gather by default,
        // unless there hasn't been one yet.
        void AddGeneralRuntimeContent(Runtime.Object content)
        {
            // Content is allowed to evaluate runtimeObject to null
            // (e.g. AuthorWarning, which doesn't make it into the runtime)
            if (content == null)
                return;
            
            if (addContentToPreviousWeavePoint) {
                previousWeavePoint.runtimeContainer.AddContent (content);
            } else {
                currentContainer.AddContent (content);
            }
        }

        void PassLooseEndsToAncestors()
        {
            if (looseEnds.Count > 0) {

                var weaveAncestor = closestWeaveAncestor;
                if (weaveAncestor) {
                    weaveAncestor.ReceiveLooseEnds (looseEnds);
                    looseEnds = null;
                }
            }
        }

        public void ReceiveLooseEnds(List<Parsed.Object> childWeaveLooseEnds)
        {
            looseEnds.AddRange (childWeaveLooseEnds);
        }

        public override void ResolveReferences(Story context)
        {
            base.ResolveReferences (context);

            foreach(var gatherPoint in gatherPointsToResolve) {
                gatherPoint.divert.targetPath = gatherPoint.targetRuntimeObj.path;
            }
                
            CheckForWeavePointNamingCollisions ();
        }

        public IWeavePoint WeavePointNamed(string name)
        {
            if (_namedWeavePoints == null)
                return null;

            IWeavePoint weavePointResult = null;
            if (_namedWeavePoints.TryGetValue (name, out weavePointResult))
                return weavePointResult;

            return null;
        }

        Weave closestWeaveAncestor {
            get {
                var ancestor = this.parent;
                while (ancestor && !(ancestor is Weave)) {
                    ancestor = ancestor.parent;
                }
                return (Weave)ancestor;
            }
        }
            
        bool WeavePointHasLooseEnd(IWeavePoint weavePoint)
        {
            // Simple choice with explicit divert 
            // definitely doesn't have a loose end
            if (weavePoint is Choice) {
                var choice = (Choice)weavePoint;

                // However, explicit gather point is definitely a loose end
                if (choice.hasExplicitGather) {
                    return true;
                }

                if (choice.hasTerminatingDivert) {
                    return false;
                }
            }

            // No content, and no simple divert above, must be a loose end.
            // (content list on Choices gets created on demand, hence how
            // it could be null)
            if (weavePoint.content == null) {
                return true;
            }

            // Detect a divert object within a weavePoint's main content
            // Work backwards since we're really interested in the end,
            // although it doesn't actually make a difference!
            else {
                for (int i = weavePoint.content.Count - 1; i >= 0; --i) {
                    var innerDivert = weavePoint.content [i] as Divert;
                    if (innerDivert && !innerDivert.isToGather) {
                        return false;
                    }
                }

                return true;
            }
        }

        // Enforce rule that weave points must not have the same
        // name as any stitches or knots upwards in the hierarchy
        void CheckForWeavePointNamingCollisions()
        {
            if (_namedWeavePoints == null)
                return;
            
            var ancestorFlows = new List<FlowBase> ();
            foreach (var obj in this.ancestry) {
                var flow = obj as FlowBase;
                if (flow)
                    ancestorFlows.Add (flow);
                else
                    break;
            }


            foreach (var namedWeavePointPair in _namedWeavePoints) {
                var weavePointName = namedWeavePointPair.Key;
                var weavePoint = (Parsed.Object) namedWeavePointPair.Value;

                foreach(var flow in ancestorFlows) {

                    // Shallow search
                    var otherContentWithName = flow.ContentWithNameAtLevel (weavePointName);

                    if (otherContentWithName && otherContentWithName != weavePoint) {
                        var errorMsg = string.Format ("{0} '{1}' has the same label name as a {2} (on {3})", 
                            weavePoint.GetType().Name, 
                            weavePointName, 
                            otherContentWithName.GetType().Name, 
                            otherContentWithName.debugMetadata);

                        Error(errorMsg, (Parsed.Object) weavePoint);
                    }

                }
            }
        }

        // Keep track of previous weave point (Choice or Gather)
        // at the current indentation level:
        //  - to add ordinary content to be nested under it
        //  - to add nested content under it when it's indented
        //  - to remove it from the list of loose ends when
        //     - it has indented content since it's no longer a loose end
        //     - it's a gather and it has a choice added to it
        IWeavePoint previousWeavePoint = null;
        bool addContentToPreviousWeavePoint = false;

        // Used for determining whether the next Gather should auto-enter
        bool hasSeenChoiceInSection = false;

        int _unnamedGatherCount;


        Runtime.Container _rootContainer;
        Dictionary<string, IWeavePoint> _namedWeavePoints;
    }
}

