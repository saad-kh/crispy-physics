using UnityEngine;
using System;
using System.Collections.Generic;

#region Factory
namespace CrispyPhysics
{
    using Internal;
    public class WorldFactory
    {
        public static IWorld CreateWorld(
            float fixedStep,
            Vector2 gravity, uint velocityIterations = 8, uint positionIterations = 3,
            float maxTranslationSpeed = 100f, float maxRotationSpeed = 360f)
        {
            return new World(
                fixedStep,
                gravity, velocityIterations, positionIterations,
                maxTranslationSpeed, maxRotationSpeed);
        }

        public static IWorld CreateWorld(WorldDefinition wordDefinition)
        {
            return new World(wordDefinition);
        }
    }
}
#endregion

namespace CrispyPhysics.Internal
{
    public class World : IWorld
    {
        #region Constructors
        public World(
            float fixedStep,
            Vector2 gravity, uint velocityIterations = 8, uint positionIterations = 3,
            float maxTranslationSpeed = 100f, float maxRotationSpeed = 360f)
        {
            if  (   fixedStep < 0
                ||  Calculus.Approximately(fixedStep, 0f))
                throw new ArgumentOutOfRangeException("Fixed Step should be stricly greater than 0");
            
            this.fixedStep = fixedStep;

            this.gravity = gravity;
            this.velocityIterations = velocityIterations;
            this.positionIterations = positionIterations;

            this.maxTranslationSpeed = maxTranslationSpeed;
            this.maxRotationSpeed = maxRotationSpeed;

            tick = 0;
            pastTick = 0;
            futurTick = 0;

            bodies = new List<Body>();
            bodyContacts = new Dictionary<Body, HashSet<Contact>>();
            contacts = new HashSet<Contact>();
            contactManager = new ContactManager(
                new BodyIteratorDelegate(BodyIterator),
                new NewPairDelegate(NewPair),
                new ContactIteratorDelegate(ContactIterator),
                new ContactHandlerDelegate(NotifyContactStartForeseen),
                new ContactHandlerDelegate(NotifyContactEndForeseen));

            step = new TimeStep(
                fixedStep, 1f / fixedStep, 1f,
                gravity, velocityIterations, positionIterations,
                maxTranslationSpeed, maxRotationSpeed);
        }

        public World(WorldDefinition worldDef) : this(
            worldDef.fixedStep,
            worldDef.gravity, worldDef.velocityIterations, worldDef.positionIterations,
            worldDef.maxTranslationSpeed, worldDef.maxRotationSpeed)
        {}
        #endregion

        #region Events
        public event IContactHandlerDelegate ContactStartForeseen;
        public event IContactHandlerDelegate ContactEndForeseen;
        public event IContactHandlerDelegate ContactStarted;
        public event IContactHandlerDelegate ContactEnded;
        #endregion

        #region Body & Contact Manager
        private uint bodyCount = 0;
        private List<Body> bodies;
        private Dictionary<Body, HashSet<Contact>> bodyContacts;
        private HashSet<Contact> contacts;
        private ContactManager contactManager;

        public IBody CreateBody(
            Vector2 position, float angle,
            BodyType type, IShape shape, float mass = 1f,
            float linearDamping = 0f, float angularDamping = 0f,
            float gravityScale = 1f,
            float friction = 0f, float restitution = 1f,
            bool sensor = false)
        {
            if (bodyCount >= uint.MaxValue)
                throw new SystemException("Maximum Body Possible to create reached");
            Body newBody = new Body(
                bodyCount++,
                tick,
                position, angle,
                type, shape, mass,
                linearDamping, angularDamping,
                gravityScale,
                friction, restitution,
                sensor
            );

            bodies.Add(newBody);
            opFlags |= OperationFlag.ExternalChange;
            futurTick = tick;
            newBody.ExternalChange += ExternalChange;

            return newBody;
        }

        public IBody CreateBody(
            Vector2 position, float angle,
            BodyDefintion bodyDef)
        {
            return CreateBody(
                position, angle,
                bodyDef.type, bodyDef.shape, bodyDef.mass,
                bodyDef.linearDamping, bodyDef.angularDamping,
                bodyDef.gravityScale, 
                bodyDef.friction, bodyDef.restitution, 
                bodyDef.sensor);
        }
        #endregion

        #region Nature
        public float fixedStep { get; private set; }

        public Vector2 gravity { get; private set; }
        public uint velocityIterations { get; private set; }
        public uint positionIterations { get; private set; }

        public float maxTranslationSpeed { get; private set; }
        public float maxRotationSpeed { get; private set; }

        public uint tick { get; private set; }
        public uint pastTick { get; private set; }
        public uint futurTick { get; private set; }

        [Flags]
        private enum OperationFlag
        {
            Locked = 0x0001,
            ExternalChange = 0x0002
        }

        private OperationFlag opFlags;
        private TimeStep step;
        #endregion

        #region Track
        public void Step(
            uint steps = 1,
            uint foreseeTicks = 0, uint bufferingTicks = 0,
            uint keepTicks = 0)
        {
            opFlags |= OperationFlag.Locked;
            tick += steps;
            if (tick >= uint.MaxValue)
                throw new SystemException("Maximum system steps reached");

            bool externalChange = (opFlags & OperationFlag.ExternalChange) == OperationFlag.ExternalChange;

            if (externalChange)
            {
                foreach (Body body in bodies)
                    body.ClearFutur(futurTick + 1);

                foreach (Contact contact in contacts)
                    contact.ClearFutur(futurTick + 1);
            }

            if (keepTicks > tick) keepTicks = tick;
            pastTick = (uint)Mathf.Max(tick - keepTicks , pastTick);

            uint iterationsToSolve = 0;
            if (futurTick < tick)
                iterationsToSolve =  
                        tick 
                    -   futurTick
                    +   (uint) Mathf.Min(foreseeTicks, bufferingTicks);
            else
                iterationsToSolve = (uint) 
                    Mathf.Min(
                        tick + foreseeTicks - futurTick,
                        bufferingTicks);

            bool lookForContact = externalChange;
            for (int i = 0; i < iterationsToSolve; i++)
            {
                futurTick++;
                
                foreach (Body body in bodies)
                {
                    if (body.futur.tick < futurTick)
                        body.Foresee(futurTick - body.futur.tick);
                    body.futur.ChangeTickDt(step.dt);
                    if (body.futur.enduringContact)
                        body.futur.changeEnduringContactState(false);
                }
                    

                foreach (Contact contact in contacts)
                    if (contact.futur.tick < futurTick)
                        contact.Foresee(futurTick - contact.futur.tick);
                    

                if (lookForContact)
                {
                    contactManager.FindNewContacts();
                    lookForContact = false;
                }

                contactManager.Collide();

                Solve(step);
            }

            foreach (Body body in bodies)
            {
                if(body.current.tick < tick)
                    body.Step(tick - body.current.tick);

                body.ForgetPast(pastTick);

                Debug.Assert(body.current.tick >= tick);
                Debug.Assert(body.futur.tick >= futurTick);
            }

            HashSet<Contact> contactsToRemove = new HashSet<Contact>();
            foreach (Contact contact in contacts)
            {
                bool wasTouching = contact.current.isTouching;

                if(contact.current.tick < tick)
                    contact.Step(tick - contact.current.tick);
                
                contact.ForgetPast(pastTick);

                Debug.Assert(contact.current.tick >= tick);
                Debug.Assert(contact.futur.tick >= futurTick);

                if(wasTouching == false && contact.current.isTouching == true)
                {
                    contact.bodyA.NotifyContactStarted(contact, EventArgs.Empty);
                    contact.bodyB.NotifyContactStarted(contact, EventArgs.Empty);

                    if (ContactStarted != null)
                        ContactStarted(contact, EventArgs.Empty);
                }

                if(wasTouching == true && contact.current.isTouching == false)
                {
                    contact.bodyA.NotifyContactEnded(contact, EventArgs.Empty);
                    contact.bodyB.NotifyContactEnded(contact, EventArgs.Empty);

                    if (ContactEnded != null)
                        ContactEnded(contact, EventArgs.Empty);
                }

                if(contact.IsDroppable())
                {
                    contactsToRemove.Add(contact);

                    Debug.Assert(bodyContacts.ContainsKey(contact.bodyA));
                    HashSet<Contact> bodyAContacts = bodyContacts[contact.bodyA];
                    Debug.Assert(bodyAContacts.Contains(contact));
                    bodyAContacts.Remove(contact);
                    if (bodyAContacts.Count == 0)
                        bodyContacts.Remove(contact.bodyA);

                    Debug.Assert(bodyContacts.ContainsKey(contact.bodyB));
                    HashSet<Contact> bodyBContacts = bodyContacts[contact.bodyB];
                    Debug.Assert(bodyBContacts.Contains(contact));
                    bodyBContacts.Remove(contact);
                    if (bodyBContacts.Count == 0)
                        bodyContacts.Remove(contact.bodyB);
                }

            }
            contacts.RemoveWhere(contact => contactsToRemove.Contains(contact));

            opFlags &= ~OperationFlag.Locked;
            opFlags &= ~OperationFlag.ExternalChange;
        }

        public void RollBack(uint toTick, uint keepTicks = 0)
        {

            opFlags |= OperationFlag.Locked;
            tick = toTick;

            if (pastTick > tick) pastTick = tick;

            if (keepTicks > tick) keepTicks = tick;
            pastTick = (uint)Mathf.Max(tick - keepTicks, pastTick);

            foreach (Body body in bodies)
            {
                body.RollBack(tick);
                body.ForgetPast(pastTick);

                Debug.Assert(body.current.tick == tick);
                Debug.Assert(body.futur.tick == futurTick);
            }

            HashSet<Contact> contactsToRemove = new HashSet<Contact>();
            foreach (Contact contact in contacts)
            {
                bool wasTouching = contact.current.isTouching;
                contact.RollBack(tick);
                contact.ForgetPast(pastTick);

                Debug.Assert(contact.current.tick == tick);
                Debug.Assert(contact.futur.tick == futurTick);

                if (wasTouching == false && contact.current.isTouching == true)
                {
                    contact.bodyA.NotifyContactStarted(contact, EventArgs.Empty);
                    contact.bodyB.NotifyContactStarted(contact, EventArgs.Empty);

                    if (ContactStarted != null)
                        ContactStarted(contact, EventArgs.Empty);
                }

                if (wasTouching == true && contact.current.isTouching == false)
                {
                    contact.bodyA.NotifyContactEnded(contact, EventArgs.Empty);
                    contact.bodyB.NotifyContactEnded(contact, EventArgs.Empty);

                    if (ContactEnded != null)
                        ContactEnded(contact, EventArgs.Empty);
                }

                if (contact.IsDroppable())
                {
                    contactsToRemove.Add(contact);

                    Debug.Assert(bodyContacts.ContainsKey(contact.bodyA));
                    HashSet<Contact> bodyAContacts = bodyContacts[contact.bodyA];
                    Debug.Assert(bodyAContacts.Contains(contact));
                    bodyAContacts.Remove(contact);
                    if (bodyAContacts.Count == 0)
                        bodyContacts.Remove(contact.bodyA);

                    Debug.Assert(bodyContacts.ContainsKey(contact.bodyB));
                    HashSet<Contact> bodyBContacts = bodyContacts[contact.bodyB];
                    Debug.Assert(bodyBContacts.Contains(contact));
                    bodyBContacts.Remove(contact);
                    if (bodyBContacts.Count == 0)
                        bodyContacts.Remove(contact.bodyB);
                }

            }
            contacts.RemoveWhere(contact => contactsToRemove.Contains(contact));

            opFlags &= ~OperationFlag.Locked;
        }

        private void Solve(TimeStep step)
        {
            Island island = new Island(
                (uint)Mathf.Max(bodies.Count, 1),
                (uint)Mathf.Max(contacts.Count, 1));

            foreach (Body body in bodies)
                body.islandBound = false;
                

            foreach (Contact contact in contacts)
                contact.islandBound = false;

            Stack<Body> stack = new Stack<Body>(bodies.Count);
            foreach (Body seed in bodies)
            {
                if (seed.islandBound)
                    continue;
                if (seed.type == BodyType.Static)
                    continue;

                island.Clear();
                stack.Push(seed);
                seed.islandBound = true;
                while (stack.Count > 0)
                {
                    Body body = stack.Pop();
                    island.Add(body);

                    if (body.type == BodyType.Static)
                        continue;

                    if(bodyContacts.ContainsKey(body))
                    {
                        HashSet<Contact> contactOfBody = bodyContacts[body];
                        foreach(Contact contact in contactOfBody)
                        {
                            if (contact.islandBound)
                                continue;
                            if (contact.futur.isTouching == false)
                                continue;
                            if (contact.bodyA.sensor || contact.bodyB.sensor)
                                continue;

                            island.Add(contact);
                            contact.islandBound = true;

                            if(contact.bodyA.islandBound == false)
                            {
                                stack.Push(contact.bodyA);
                                contact.bodyA.islandBound = true;
                            }

                            if (contact.bodyB.islandBound == false)
                            {
                                stack.Push(contact.bodyB);
                                contact.bodyB.islandBound = true;
                            }
                        }
                    }
                }

                island.Solve(step);

                foreach (Body islandBody in island.BodyIterator())
                    if (islandBody.type == BodyType.Static)
                        islandBody.islandBound = false;
            }

            contactManager.FindNewContacts();
        }
        #endregion

        #region Crisp
        private void Crisp()
        {
            /*if(tick >= actionTick)
            {
                tick = tick - actionTick;
                opFlags |= OperationFlag.Crisped;
                foreach (IBody body in bodies)
                {
                    Vector2 crispedPosition;

                    if (Mathf.Sign(body.position.x * body.linearVelocity.x) >= 0)
                        crispedPosition.x = 
                                Mathf.Ceil(Mathf.Abs(body.position.x / crispSize)) 
                            *   crispSize * Mathf.Sign(body.position.x);
                    else
                        crispedPosition.x = 
                                Mathf.Floor(Mathf.Abs(body.position.x / crispSize)) 
                            *   crispSize * Mathf.Sign(body.position.x);

                    if (Mathf.Sign(body.position.y * body.linearVelocity.y) >= 0)
                        crispedPosition.y = 
                                Mathf.Ceil(Mathf.Abs(body.position.y / crispSize)) 
                            *   crispSize * Mathf.Sign(body.position.y);
                    else
                        crispedPosition.y = 
                                Mathf.Floor(Mathf.Abs(body.position.y / crispSize)) 
                            *   crispSize * Mathf.Sign(body.position.y);

                    body.ChangeSituation(crispedPosition, body.angle);
                }
            }*/
        }
        #endregion

        #region Event Handlers & Delegates

        private void ExternalChange(IBody body, uint fromTick)
        {
            if ((opFlags & World.OperationFlag.ExternalChange) == 0)
            {
                futurTick = (uint)Mathf.Max(tick, Mathf.Min(futurTick, fromTick));
                opFlags |= World.OperationFlag.ExternalChange;
            }
        }


        private IEnumerable<Body> BodyIterator(uint start = 0, uint end = 0)
        {
            if (end == 0)
                end = (uint)bodies.Count;
            for (int i = (int)start; i < (int)end; i++)
                yield return bodies[i];
        }

        private void NewPair(Body bodyA, Body bodyB)
        {
            if (bodyA == bodyB)
                return;

            HashSet<Contact> bodyAContacts = null;
            HashSet<Contact> bodyBContacts = null;

            if (bodyContacts.ContainsKey(bodyA))
            {
                bodyAContacts = bodyContacts[bodyA];
                foreach (Contact contact in bodyAContacts)
                    if (contact.bodyA == bodyB || contact.bodyB == bodyB)
                        return;
            }
            else
            {
                bodyAContacts = new HashSet<Contact>();
                bodyContacts.Add(bodyA, bodyAContacts);
            }

            if (bodyContacts.ContainsKey(bodyB))
                bodyBContacts = bodyContacts[bodyB];
            else
            {
                bodyBContacts = new HashSet<Contact>();
                bodyContacts.Add(bodyB, bodyBContacts);
            }

            Debug.Assert(bodyA.futur.tick == bodyB.futur.tick);
            Contact newContact = ContactFactory.CreateContact(bodyA.futur.tick, bodyA, bodyB);
            bodyAContacts.Add(newContact);
            bodyBContacts.Add(newContact);
            contacts.Add(newContact);
        }

        private IEnumerable<Contact> ContactIterator()
        {
            foreach (Contact contact in contacts)
                yield return contact;
        }

        private void NotifyContactStartForeseen(Contact contact, EventArgs args)
        {
            contact.bodyA.NotifyContactStartForeseen(contact, args);
            contact.bodyB.NotifyContactStartForeseen(contact, args);

            if (ContactStartForeseen != null)
                ContactStartForeseen(contact, args);
        }

        private void NotifyContactEndForeseen(Contact contact, EventArgs args)
        {
            contact.bodyA.NotifyContactStartForeseen(contact, EventArgs.Empty);
            contact.bodyB.NotifyContactStartForeseen(contact, EventArgs.Empty);

            if (ContactEndForeseen != null)
                ContactEndForeseen(contact, args);
        }
        #endregion
    }
}